# S3 and MinIO access from your application

Use an `s3` or `minio` dependency when your application writes objects that a `storage-assert.s3` step checks. The `s3` kind selects an S3-compatible backend, currently RustFS. The `minio` kind selects MinIO. Both use the same fixed credentials for disposable local tests.

## Client settings

| Setting | Value |
| --- | --- |
| Access key | `vouchfx-minio` |
| Secret key | `vouchfx-minio-secret` |
| Endpoint in an application service | `http://${conn:objects}` for a dependency named `objects` |
| Addressing | Path style: the bucket belongs in the URL path |

These are public test credentials set by the engine, not production credentials. Configure your application's test S3 client with both keys, the endpoint and path-style addressing. Changing the dependency from `minio` to `s3` preserves the keys.

The service-facing `${conn:objects}` expression supplies **host and port only**. It does not contain an HTTP scheme or credentials. `${conn:objects.host}` and `${conn:objects.port}` are also available; `.accessKey`, `.secretKey`, `.username` and `.password` are unsupported for these two kinds.

## Environment fragment

Add this fragment to your suite and replace the application image. `S3_ENDPOINT` is an application setting; configure your code to read it. The AWS credential variable names work with clients that support the [AWS environment credential provider](https://docs.aws.amazon.com/sdkref/latest/guide/feature-static-credentials.html).

```yaml
environment:
  dependencies:
    objects:
      type: s3
  services:
    writer:
      image: your-writer:dev
      env:
        S3_ENDPOINT: "http://${conn:objects}"
        AWS_ACCESS_KEY_ID: vouchfx-minio
        AWS_SECRET_ACCESS_KEY: vouchfx-minio-secret
```

Configure path-style addressing in the application client; the environment fragment alone does not select it. Create the bucket through your application or fixture setup before writing objects. Point `storage-assert.s3.target` at `objects`; the assertion checks objects rather than creating the bucket.

## Application and assertion connections

The assertion provider receives a separate engine-built connection string:

```text
ServiceURL=http://<host>:<port>;AccessKey=vouchfx-minio;SecretKey=vouchfx-minio-secret
```

The engine stages that connection for the test step. Do not put this whole string into the application's `${conn:objects}` setting or replace the allocated endpoint with a fixed host port.

The dependency owns its authentication variables: `MINIO_ROOT_USER`/`MINIO_ROOT_PASSWORD` for `minio`, and `RUSTFS_ACCESS_KEY`/`RUSTFS_SECRET_KEY` for `s3`. Authoring those keys in the dependency's `env` map is refused. Put the application client settings in the application's service `env` map instead.

## Object absence and diagnostics

An object HEAD returning 404 does not establish that its bucket exists. The assertion sends one bucket HEAD through the same client and cancellation budget before treating the object as absent. A successful bucket HEAD permits ordinary absence: `expect.exists: true` (the default) fails, while `expect.exists: false` passes. An unavailable, missing or forbidden bucket is an environment error; it cannot satisfy an absence assertion. RETRY confirms absence on every attempt.

This confirmation may require bucket-list permission on an ambiguous 404. An existing object that passes HEAD needs no bucket HEAD or additional bucket-list permission. The provider does not list buckets, cache bucket confirmation or add an independent retry budget.

Mismatch observations identify the field and `matched: false`. They omit resolved expected values and returned metadata or content-type strings. Actual byte counts and calculated SHA-256 digests remain available, and the existing `etag`, `versionId`, `size` and `exists` captures retain their meanings. Body content and searched substrings are never included in observations. Historical expected/actual diff observations still render.

## Architecture coverage

The default RustFS image index contains Linux amd64 and arm64 manifests. The repository records runtime checks for amd64 only; arm64 runtime acceptance remains unverified. This limitation concerns the default `s3` backend, not a claim about every image override or all dependency kinds.

See the [generated S3 assertion reference](language-reference.md#storage-asserts3) and [dependency environment patterns](common-patterns.md) for step options and other connection expressions.
