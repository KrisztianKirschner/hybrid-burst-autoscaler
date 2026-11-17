# Source image set

The images every job processes: ESA/Webb images, normalised to exactly **5472 × 3648** (3:2, 20 MP), JPEG q90, no metadata. The files are not in git (about 25 MB). They live under `src/img-01.jpg` … `src/img-07.jpg` in **both** buckets named `hybrid-burst-data`: RustFS on `obs-01` for the on-prem workers, and S3 in AWS for the burst workers ("storage follows the worker"). Both must hold the same files, or the two tiers would process different images.

**Current set: v2** (7 images, all used). `loadtest/calibrate.py` records the set version with every job (`SOURCE_SET`), so measurements on different sets are never mixed.

**Frozen (2026-10-23)** together with preset v1: the measured runs use this set unchanged. Changing an image means a new set version and a new calibration. Result and criteria: [docs/app.md](../../docs/app.md) §5.

## Files

| File | Contents |
|---|---|
| `make-source-set.ps1` | builds the set from the downloaded originals (Windows PowerShell, `System.Drawing`, no extra installs); its default `-Names` is the current set |
| `credits-v2.csv` | the current set: original file, source page, crop window, size, SHA-256; the `credit` column is the credit line from each source page (filled in on 2026-10-23) |
| `credits-v1.csv` | the previous set, kept because the v0 and v1 calibration data were measured on it |

## Versions

| Set | Images | Why it changed |
|---|---|---|
| v1 | 8: carinanebula3, CEERS11, potm2606a, weic2303a, weic2425a, weic2428a, weic2518a, weic2520a | first set; weic2520a (img-08) was excluded from all tests: about 1.7× the set median in `large` |
| **v2** | 7: carinanebula3, pillarsofcreation_composite, potm2606a, potm2607a, weic2425a, weic2428a, weic2518a | with `medium` at 3600 px, CEERS11 and weic2303a measured at 0.82 / 0.81 × the set median, below the fair zone (0.85–1.15). Replaced by two images of medium detail; weic2520a dropped. |

The numbering follows the alphabetical order of the originals, so `img-02` is a different image in v1 and v2. Compare images across sets by their original name (`credits-v*.csv`), not by number.

## How the set is built

1. **Download** the *Large JPEG* of each image (`https://cdn.esawebb.org/archives/images/large/<id>.jpg`), not the *Fullsize Original*, which is a TIFF; the pipeline decodes only JPEG and PNG.
2. **Portrait images are rotated** 90°.
3. **A 3:2 crop window**, as large as fits but at most 1.5× the output size, so detail per output pixel stays comparable. The original must be at least 5472 × 3648 (in either orientation), or the image would be scaled up.
4. **Several window positions are tried per image.** Each is encoded, and its JPEG size serves as a proxy for detail and therefore encode cost. The script keeps, per image, the window closest to a common target size: the one that keeps the most images within ±15 %.
5. **Output:** `img-NN.jpg` plus `credits.csv` (copy it here as `credits-vN.csv`).

```powershell
./make-source-set.ps1 -SourceDir $env:USERPROFILE\Downloads -OutDir $env:USERPROFILE\Downloads\hba-source-set-v2
```

Upload the `img-NN.jpg` files to `src/` in **both** buckets (RustFS and S3; remove objects that are no longer in the set), and compare their SHA-256 with the manifest.

**Choosing new images:** the file size after normalisation predicts the cost well. In `medium` v1, images at 2.7–2.9 MB measured 0.81–0.90 × the set median, images at 3.2–4.6 MB 0.98–1.09. Pick images that land at about 3.2–4.3 MB: nebulae or galaxy fields that fill the frame, not much black sky. The `outlier` column in the manifest is that proxy (±15 % of the target), not a decision: img-05/06 are flagged there but measured fair.

## Licence

ESA/Webb images may be used under the conditions on [esawebb.org/copyright](https://esawebb.org/copyright/) (CC BY 4.0 for ESA/Webb releases). Every use needs the credit line from the image's page (`source_page` in the manifest), e.g. in the report and the repository README. Example, weic2425a: "ESA/Webb, NASA & CSA, P. Zeidler, E. Sabbi, A. Nota, M. Zamani (ESA/Webb)". The credit lines of all seven images are in `credits-v2.csv`, taken from their pages on 2026-10-23 (`potm2607a` also has an acknowledgement line, kept in the same field).

Space images are not typical of an image service (large dark areas, little texture outside the nebulae). For the thesis that matters little, since the subject is how autoscaling behaves, not image processing; it is named in the limitations.
