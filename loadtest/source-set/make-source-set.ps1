# Builds the calibration source set: every image becomes exactly OutW x OutH (3:2), JPEG q90, no metadata.
#  1. Portrait sources are rotated 90 degrees.
#  2. Crop window: 3:2, as large as fits, but at most MaxDownscale x the output size, so detail per
#     output pixel stays comparable between sources.
#  3. Several window positions are tried per image. Each candidate is encoded; its JPEG size is the
#     proxy for detail (and so for encode cost).
#  4. Target size T = median of the per-image maxima. Each image keeps the window closest to T.
#     Images that still end up far from T are flagged as outliers.
param(
    [string]$SourceDir = "$env:USERPROFILE\Downloads",
    [string]$OutDir = "$env:USERPROFILE\Downloads\hba-source-set-v2",
    # Set v2. Dropped from v1: weic2520a (too expensive), CEERS11 and weic2303a (too cheap in medium)
    [string[]]$Names = @('carinanebula3.jpg', 'pillarsofcreation_composite.jpg', 'potm2606a.jpg', 'potm2607a.jpg',
                         'weic2425a.jpg', 'weic2428a.jpg', 'weic2518a.jpg'),
    [int]$OutW = 5472,
    [int]$OutH = 3648,
    [double]$MaxDownscale = 1.5,
    [long]$Quality = 90,
    [double]$OutlierTolerance = 0.15
)

Add-Type -AssemblyName System.Drawing
$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
$encoderParams = [System.Drawing.Imaging.EncoderParameters]::new(1)
$encoderParams.Param[0] = [System.Drawing.Imaging.EncoderParameter]::new([System.Drawing.Imaging.Encoder]::Quality, $Quality)

function Get-CandidateJpeg($src, [int]$x, [int]$y, [int]$w, [int]$h) {
    $dst = [System.Drawing.Bitmap]::new($OutW, $OutH, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $attrs = [System.Drawing.Imaging.ImageAttributes]::new()
    $ms = [System.IO.MemoryStream]::new()
    try {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $attrs.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)   # no dark fringe at the edges
        $g.DrawImage($src, [System.Drawing.Rectangle]::new(0, 0, $OutW, $OutH), $x, $y, $w, $h,
            [System.Drawing.GraphicsUnit]::Pixel, $attrs)
        $dst.Save($ms, $codec, $encoderParams)   # new bitmap: no EXIF/ICC copied
        , $ms.ToArray()
    }
    finally { $ms.Dispose(); $attrs.Dispose(); $g.Dispose(); $dst.Dispose() }
}

function Get-Positions([int]$free, [int]$count) {
    if ($free -le 0 -or $count -le 1) { return , 0 }
    , (0..($count - 1) | ForEach-Object { [int][math]::Round($free * $_ / ($count - 1)) })
}

# Pass 1: encode every candidate window
$images = foreach ($name in ($Names | Sort-Object)) {
    $src = [System.Drawing.Image]::FromFile((Join-Path $SourceDir $name))
    try {
        $origW = $src.Width; $origH = $src.Height
        $rotated = $src.Height -gt $src.Width
        if ($rotated) { $src.RotateFlip([System.Drawing.RotateFlipType]::Rotate90FlipNone) }

        $h = [int][math]::Floor([math]::Min([math]::Min($src.Height, $src.Width / 1.5), $OutH * $MaxDownscale))
        $w = [int][math]::Round($h * 1.5)
        $freeX = $src.Width - $w; $freeY = $src.Height - $h
        $grid = if ($freeX -gt 0 -and $freeY -gt 0) { 3 } else { 5 }

        $candidates = foreach ($x in (Get-Positions $freeX $grid)) {
            foreach ($y in (Get-Positions $freeY $grid)) {
                $bytes = Get-CandidateJpeg $src $x $y $w $h
                [pscustomobject]@{ X = $x; Y = $y; Bytes = $bytes; Size = $bytes.Length }
            }
        }
        Write-Host ("{0}: {1} candidates, window {2}x{3}" -f $name, @($candidates).Count, $w, $h)
        [pscustomobject]@{
            Name = $name; OrigW = $origW; OrigH = $origH; Rotated = $rotated; W = $w; H = $h
            Candidates = @($candidates); Max = ($candidates | Measure-Object Size -Maximum).Maximum
        }
    }
    finally { $src.Dispose() }
}

# Pass 2: target = the candidate size that keeps the most images within tolerance
# (tie-break: smallest summed relative deviation); each image keeps its window closest to it
$best = $null
foreach ($t in ($images.Candidates.Size | Sort-Object -Unique)) {
    $devs = foreach ($img in $images) { ($img.Candidates | ForEach-Object { [math]::Abs($_.Size / $t - 1) } | Measure-Object -Minimum).Minimum }
    $hits = @($devs | Where-Object { $_ -le $OutlierTolerance }).Count
    $sum = ($devs | Measure-Object -Sum).Sum
    if (-not $best -or $hits -gt $best.Hits -or ($hits -eq $best.Hits -and $sum -lt $best.Sum)) { $best = @{ T = $t; Hits = $hits; Sum = $sum } }
}
$target = $best.T
$images | ForEach-Object { "{0}: candidates {1:N2}-{2:N2} MB" -f $_.Name, (($_.Candidates.Size | Measure-Object -Minimum).Minimum / 1MB), ($_.Max / 1MB) }

New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item (Join-Path $OutDir '*.jpg'), (Join-Path $OutDir 'credits.csv') -ErrorAction SilentlyContinue

$n = 0
$rows = foreach ($img in $images) {
    $n++
    $best = $img.Candidates | Sort-Object { [math]::Abs($_.Size - $target) } | Select-Object -First 1
    $file = 'img-{0:D2}.jpg' -f $n
    [System.IO.File]::WriteAllBytes((Join-Path $OutDir $file), $best.Bytes)
    $ratio = $best.Size / $target
    [pscustomobject]@{
        file = $file
        original = $img.Name
        source_page = 'https://esawebb.org/images/{0}/' -f [System.IO.Path]::GetFileNameWithoutExtension($img.Name).ToLowerInvariant()
        credit = ''
        original_px = '{0}x{1}' -f $img.OrigW, $img.OrigH
        rotated = $img.Rotated
        crop = '{0},{1},{2}x{3}' -f $best.X, $best.Y, $img.W, $img.H
        mb = [math]::Round($best.Size / 1MB, 2)
        vs_target = [math]::Round($ratio, 2)
        outlier = [math]::Abs($ratio - 1) -gt $OutlierTolerance
        sha256 = (Get-FileHash (Join-Path $OutDir $file) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$rows | Export-Csv (Join-Path $OutDir 'credits.csv') -NoTypeInformation -Encoding utf8
"target = {0:N2} MB" -f ($target / 1MB)
$rows | Format-Table file, original, original_px, rotated, crop, mb, vs_target, outlier -AutoSize
