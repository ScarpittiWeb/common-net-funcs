---
name: commonnetfuncs-web-aws-s3
description: 'Use CommonNetFuncs.Web.Aws.S3 for S3 upload, download, multipart, listing, and URLs. Consult for compression defaults, failure handling, IAM, and stream ownership.'
---

# CommonNetFuncs.Web.Aws.S3

Configure an AWS SDK `IAmazonS3` with the intended region and credentials,
then inject `IAwsS3`/`ApiAwsS3` or use the static extensions on the client.

## Workflow

1. Choose a private bucket, least-privilege IAM policy, trusted object key, and
   upload/download overload. Use `UploadS3File`, `GetS3File`, and
   `GetAllS3BucketFiles` as needed.
2. Inspect upload's `bool`. A stream upload requires a usable `Length`. Stream
   ownership is inconsistent: an under-threshold stream upload with
   `compressSteam: false` disposes the supplied stream, while other upload
   paths may leave it open. Do not rely on the stream remaining usable after
   the call; check the selected path and coordinate disposal with the caller.
3. Treat download/list results carefully: `GetS3File` returns only `Task` and
   can log and swallow failures; listing can return an empty list on failure.
   Verify the outcome separately, or use the AWS SDK directly when failures
   must propagate.

## Important constraints

- Stream uploads default to GZip (`compressSteam: true`) below the default
  10 MiB multipart threshold. At or above that threshold the multipart branch
  uploads the original stream without applying those compression options.
  Choose settings and downstream decoding with that difference in mind.
- Stream downloads default to `decompressGzipData: true`. Validate data size
  and decompression limits, and do not assume every stored object has identical
  content encoding.
- Public object URLs are not authorization. Prefer private access, constrained
  presigned URLs where appropriate, and scoped credentials. Protect local
  download paths and scope any bucket-validation cache to the relevant client
  and account.

See [IAwsS3.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Aws.S3/IAwsS3.cs),
[AwsS3HelpersStatic.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Aws.S3/AwsS3HelpersStatic.cs),
and the [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Aws.S3/README.md).
