using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Hba.Worker;

//summary
//JobStorage.cs handles image files in object storage:
//      -It chooses MinIO for onprem workers or Amazon S3 for aws workers.
//      -It downloads a source image by key and returns its bytes.
//      -It uploads processed image bytes with the output content type.
//      -On AWS, it uses the SDK’s default credential chain, so credentials can come from the instance profile; on-prem uses the configured MinIO endpoint and credentials.


public interface IJobStorage
{
    Task<byte[]> GetSourceAsync(string key, CancellationToken cancellationToken);

    Task PutOutputAsync(
        string key,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken);
}

public sealed class S3JobStorage : IJobStorage, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;

    public S3JobStorage(WorkerOptions options)
    {
        _bucket = options.Bucket;
        if (options.Tier == WorkerOptions.TierOnPrem)
        {
            _client = new AmazonS3Client(
                new BasicAWSCredentials(options.OnPremAccessKey, options.OnPremSecretKey),
                new AmazonS3Config
                {
                    ServiceURL = options.OnPremEndpoint,
                    ForcePathStyle = true
                });
        }
        else
        {
            _client = new AmazonS3Client(RegionEndpoint.GetBySystemName(options.AwsRegion));
        }
    }

    public async Task<byte[]> GetSourceAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await _client.GetObjectAsync(
            new GetObjectRequest { BucketName = _bucket, Key = key },
            cancellationToken);
        await using var output = new MemoryStream();
        await response.ResponseStream.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    public async Task PutOutputAsync(
        string key,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken)
    {
        await using var input = new MemoryStream(content, writable: false);
        await _client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = input,
                ContentType = contentType
            },
            cancellationToken);
    }

    public void Dispose() => _client.Dispose();
}
