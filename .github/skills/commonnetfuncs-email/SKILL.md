---
name: commonnetfuncs-email
description: 'Use CommonNetFuncs.Email for SMTP messages, HTML, attachments, and background email jobs. Consult for ownership, send failures, and HTML safety.'
---

# CommonNetFuncs.Email

Use `IEmailService`/`EmailService` or `Email.SendEmail` with `SmtpSettings`,
`EmailAddresses`, and `EmailContent` or `EmailContentBytes`.

## Workflow

1. Configure the SMTP host, credentials, and appropriate transport security;
   build validated sender/recipient addresses and set `BodyIsHtml` deliberately.
2. Await `SendEmail` and inspect the returned `bool`. Invalid arguments can
   throw, and cancellation may propagate; a completed task is not necessarily
   a successful send.
3. Use byte-based content/attachments for serialized Hangfire jobs rather than
   live streams. Protect credentials and message data in configuration and logs.

## Ownership and HTML safety

- `MailAttachment(Stream)` takes ownership of the supplied stream. Disposing
  the attachment disposes it; `EmailContent.AutoDisposeAttachments` defaults
  to `true`. `MailAttachment.GetStream()` produces a new stream that its
  consumer must dispose.
- `StringToHtml` encodes its text, but table cells/headers and
  `CreateHtmlLink` interpolate values without HTML encoding; custom CSS is
  inserted verbatim. Encode untrusted text and attribute values, validate URL
  schemes, and do not accept untrusted CSS.
- The current `BuildHtmlEmail` overloads that take a `string` body and either
  an optional footer or a `DataTable` append the body twice. Verify the
  rendered result if using either overload.
- Read receipts disclose the configured receipt address. Attachments and
  rendered HTML can contain sensitive data; apply appropriate size limits and
  content handling.

See [EmailService.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Email/EmailService.cs),
[Email.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Email/Email.cs), and the
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Email/README.md).
