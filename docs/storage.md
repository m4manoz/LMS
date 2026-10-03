# File storage

Course files (images, PDFs, video, audio, downloads) and assignment submissions are stored through one interface.
Two providers are available: local disk (default) and S3-compatible object storage.

## Choosing a provider

Set `Storage:Provider` to `Local` or `S3`. The choice is per deployment, not per organization. Every object key starts
with the tenant id, so organizations stay separate inside one bucket.

| Setting | Meaning | Default |
|---|---|---|
| `Storage:Provider` | `Local` or `S3` | `Local` |
| `Storage:LocalRoot` | Folder for local files | `content-assets` |
| `Storage:LocalFallback` | With S3, still serve files that were uploaded to local disk earlier | `true` |
| `Storage:S3:Bucket` | Bucket name (required for S3) | |
| `Storage:S3:Region` | AWS region | `us-east-1` |
| `Storage:S3:ServiceUrl` | Endpoint of a self-hosted server. Leave empty for AWS S3 | |
| `Storage:S3:ForcePathStyle` | `true` for MinIO and most self-hosted servers | `false` |
| `Storage:S3:KeyPrefix` | Folder inside the bucket | `lms/` |
| `Storage:S3:PresignSeconds` | How long a direct media link works (60 to 86400) | `3600` |
| `Storage:S3:AccessKeyId` / `SecretAccessKey` | Credentials (development) | |
| `Storage:S3:AccessKeyIdReference` / `SecretAccessKeyReference` | Names of secrets in the secret manager (production) | |
| `Storage:S3:CreateBucket` | Create the bucket at startup if missing (development only) | `false` |

If both credentials are left empty, the AWS default credential chain is used (environment variables, shared profile,
or an IAM role on the host). That is the recommended setup on AWS.

The API refuses to start if `Storage:Provider` is `S3` and the settings are incomplete, and `/health/ready` returns
503 while the bucket cannot be reached.

## AWS S3

```
Storage__Provider=S3
Storage__S3__Bucket=my-lms-files
Storage__S3__Region=ap-south-1
```

Create a private bucket with "Block all public access" on and default encryption enabled. The role or user needs:

```json
{ "Version": "2012-10-17", "Statement": [
  { "Effect": "Allow", "Action": ["s3:GetObject", "s3:PutObject"], "Resource": "arn:aws:s3:::my-lms-files/lms/*" },
  { "Effect": "Allow", "Action": ["s3:ListBucket"], "Resource": "arn:aws:s3:::my-lms-files", "Condition": { "StringLike": { "s3:prefix": ["lms/*"] } } }
] }
```

## Self-hosted (development)

`docker compose -f infra/s3/docker-compose.yml up -d` starts an S3-compatible server on port 9000. The compose file
lists the matching `Storage__*` settings. MinIO works with the same settings (`ForcePathStyle=true`); its community
images are no longer published to public registries, which is why the compose file uses RustFS.

## How files are served

- **Through the API** (`GET /api/v1/tenant/courses/{course}/assets/{asset}`): works with every provider. Learners must
  be enrolled; staff can always read. Sent with `X-Content-Type-Options: nosniff`.
- **Signed link** (`.../assets/{asset}/link`): with S3, returns a short-lived URL so images, video, audio and PDFs
  stream straight from storage, including seeking in long videos. The same access rules apply when the link is
  requested. The link itself is a credential for its lifetime, so it is never cached and is not logged. The type is
  forced to the one validated at upload and files are shown inline only for images, PDFs, video and audio. Everything
  else is sent as an attachment.

## Moving from local disk to S3

Switch `Storage:Provider` to `S3` with `LocalFallback` left on. New files go to S3; old files keep working from disk.
To move the old files, copy the folder into the bucket under the key prefix, then turn the fallback off:

```
aws s3 sync content-assets/ s3://my-lms-files/lms/
```

Database records need no change because they store the same logical key either way.

## Testing against a real server

The normal test run uses an in-memory store. Six extra tests run against a real server when these are set:

```
set LMS_TEST_S3_URL=http://localhost:9000
set LMS_TEST_S3_ACCESS_KEY=lmsadmin
set LMS_TEST_S3_SECRET_KEY=lmsadmin-secret
dotnet test tests/Lms.Api.Tests
```

## Not covered yet

- Separate buckets or credentials per organization.
- Deleting objects when courses or submissions are removed (files are kept).
- Virus scanning of uploads.
- Resumable or multipart browser-to-storage uploads (uploads still pass through the API, up to 100 MB).
