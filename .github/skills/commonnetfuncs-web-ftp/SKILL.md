---
name: commonnetfuncs-web-ftp
description: 'Use CommonNetFuncs.Web.Ftp for SSH.NET SFTP connections and file transfers. Consult for password authentication, host-key verification, paths, and client ownership.'
---

# CommonNetFuncs.Web.Ftp

Despite the package name, `SshFtp` and `SshFtpService` use SFTP over SSH, not
FTP or FTPS.

## Workflow

1. Configure `FileTransferConnection` with the SFTP host, port, username,
   password, and buffer size. `Connect`/`ConnectAsync` return a newly created
   `SftpClient`; retain and dispose that returned instance.
2. Perform listing, upload, download, deletion, or CSV operations only while
   connected; handle `SshConnectionException` and inspect returned success
   values. Dispose the service when using the `ISshFtpService` wrapper.
3. Keep credentials outside source control, constrain remote paths, and limit
   transfer sizes.

## Security boundaries

- The convenience connection helper constructs an SSH.NET client with a
  password. It exposes no host-key fingerprint/callback configuration, and
  `ConnectionProtocol` is not used to select another protocol. Do not assume
  it pins a trusted server key.
- Where strict host-key verification or key-based authentication is required,
  configure an appropriate SSH.NET client/implementation separately rather
  than assuming this helper supplies it.
- Reconnecting disconnects an existing client but does not dispose it. Manage
  old and new client lifetimes explicitly; avoid sharing mutable connections
  across tenants or concurrent work without a suitable ownership policy.

See [SshFtp.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Ftp/SshFtp.cs),
[SshFtpService.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Ftp/SshFtpService.cs), and the
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Ftp/README.md).
