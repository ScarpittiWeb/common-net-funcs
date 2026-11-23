# GitHub Actions CI image

This image uses Ubuntu 24.04 LTS (`noble`) and installs the latest FFmpeg
package available from the Ubuntu repositories. It does not use a third-party
PPA. The workflow installs the required .NET SDK with `actions/setup-dotnet`.

From the repository root, build and verify it:

```powershell
docker build --tag zeshue/ubuntu-noble-ffmpeg:1.0.0 --file docker\ci\Dockerfile docker\ci
docker run --rm zeshue/ubuntu-noble-ffmpeg:1.0.0 ffmpeg -version
```

Push it to Docker Hub:

```powershell
docker login
docker push zeshue/ubuntu-noble-ffmpeg:1.0.0
```

After the tag is available, update the `container.image` value in
`.github/workflows/all-tests.yml` to
`zeshue/ubuntu-noble-ffmpeg:1.0.0`.
