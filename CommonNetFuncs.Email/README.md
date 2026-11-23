# CommonNetFuncs.Email

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Email)](https://www.nuget.org/packages/CommonNetFuncs.Email/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Email)](https://www.nuget.org/packages/CommonNetFuncs.Email/)

SMTP email configuration, attachment handling, service wrappers, and HTML-building helpers for .NET. The package targets `net11.0` and `netstandard2.1`. `HangfireEmailService` is available only in the `net11.0` asset because `CommonNetFuncs.Hangfire` requires ASP.NET Core.

## Contents

- [CommonNetFuncs.Email](#commonnetfuncsemail)
  - [Contents](#contents)
  - [Installation](#installation)
  - [Sending email](#sending-email)
    - [Configuration types](#configuration-types)
    - [`Email.SendEmail` overloads](#emailsendemail-overloads)
    - [Services and Hangfire wrapper](#services-and-hangfire-wrapper)
  - [Attachment ownership and methods](#attachment-ownership-and-methods)
  - [HTML email helpers](#html-email-helpers)
  - [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.Email
```

## Sending email

### Configuration types

```csharp
using System.IO.Compression;
using System.Net;
using CommonNetFuncs.Email;
using MailKit.Security;

SmtpSettings smtp = new(
    smtpServer: "smtp.example.com",
    smtpPort: 587,
    smtpUser: "mailer",
    smtpPassword: "secret",
    connectionTimeout: TimeSpan.FromSeconds(10))
{
    SecureSocketOptions = SecureSocketOptions.StartTls,
    RequireTLS = true,
    MaxAttempts = 3,
    RetryDelay = TimeSpan.FromMilliseconds(250),
    // ClientCertificates, ProxyClient, LocalEndPoint, and LocalDomain are optional.
};

// The full retry constructor is also public:
SmtpSettings smtpWithRetry = new(
    "smtp.example.com", 587, "mailer", "secret",
    TimeSpan.FromSeconds(10), maxAttempts: 3, retryDelay: TimeSpan.Zero);

EmailAddresses addresses = new(
    fromAddress: new MailAddress("Sender", "sender@example.com"),
    toAddresses: [new MailAddress("Recipient", "recipient@example.com")],
    ccAddresses: [new MailAddress("Copy", "copy@example.com")],
    bccAddresses: [new MailAddress("Audit", "audit@example.com")]);

byte[] bytes = File.ReadAllBytes("report.pdf");
using MailAttachment streamAttachment = new(
    "report.pdf", new MemoryStream(bytes), CompressionLevel.Fastest);
MailAttachmentBytes serializedAttachment = new(
    "report.pdf", bytes, CompressionLevel.Optimal);

EmailContent content = new(
    subject: "Monthly report",
    body: "<p>Attached.</p>",
    bodyIsHtml: true,
    attachments: [streamAttachment],
    autoDisposeAttachments: true,
    zipAttachments: false);

EmailContentBytes serializableContent = new(
    "Monthly report", "<p>Attached.</p>", true,
    [serializedAttachment], zipAttachments: false);

SendEmailConfig config = new(
    smtp, addresses, content,
    readReceipt: true, readReceiptEmail: "receipts@example.com");

SendEmailConfigBytes serializableConfig = new(
    smtp, addresses, serializableContent,
    readReceipt: true, readReceiptEmail: "receipts@example.com");
```

`MailAddress` has a `Name` and an `Email`; the latter has data-annotation constraints, and the sender's and recipients' email strings are validated by `Email.SendEmail`. At least one non-null To, Cc, or Bcc recipient is required and the subject is required. `EmailAddresses` arrays and the config/content instances are mutable.

`SmtpSettings` defaults to a five-second connection timeout, eight total attempts, and a 500 ms retry delay. `MaxAttempts` must be at least one and `RetryDelay` cannot be negative. If both credentials are nonblank, the client authenticates and defaults to `SecureSocketOptions.StartTls`; otherwise it defaults to `SecureSocketOptions.None`. An explicit `SecureSocketOptions` overrides that default. `ClientCertificates`, `ProxyClient`, `LocalEndPoint`, `LocalDomain`, and `RequireTLS` are applied to each MailKit client.

### `Email.SendEmail` overloads

```csharp
CancellationToken token = CancellationToken.None;

bool sent = await Email.SendEmail(config, token);
bool sentFromSerializableConfig = await Email.SendEmail(serializableConfig, token);
```

The methods retry SMTP connection/send failures up to `MaxAttempts`, delaying between failed attempts. Most validation, cancellation, serialization, attachment, and SMTP failures are logged and result in `false`; inspect the Boolean result rather than assuming completion means success. Some direct property/constructor validation (for example an invalid `MaxAttempts`) can still throw.

The SMTP client sends the body as text or HTML according to `BodyIsHtml`; it does not sanitize HTML. A read-receipt request is emitted only when `ReadReceipt` is true and `ReadReceiptEmail` is nonblank. On failure, logging redacts `SmtpPassword` but serializes configuration details including addresses, subject/body information, and attachment-related state when possible. Avoid logging configuration containing sensitive content. **The logging path also mutates `SmtpSettings.SmtpPassword` to `"REDACTED"` when it serializes a failure**, so do not expect to reuse the same settings for a later send without restoring the password.

Attachment handling is performed before SMTP attempts. Attachments are always stored internally as Gzip-compressed data and are decompressed when added to the MIME message; `ZipAttachments` then optionally wraps the resulting files in one `Files.zip` attachment. Attachment-processing exceptions are logged by `AddAttachments` and swallowed, so verify the resulting message or attachments at the application level when partial attachment loss is unacceptable.

### Services and Hangfire wrapper

Register either concrete service as needed:

```csharp
using CommonNetFuncs.Email;
using Microsoft.Extensions.DependencyInjection;

IServiceCollection services = new ServiceCollection();
services.AddSingleton<IEmailService, EmailService>();
IServiceProvider provider = services.BuildServiceProvider();
IEmailService service = provider.GetRequiredService<IEmailService>();
```

`EmailService` implements all six `IEmailService.SendEmail` overloads and delegates to the corresponding static method. The overloads are demonstrated below:

```csharp
bool a = await service.SendEmail(
    smtp, addresses, content, readReceipt: true,
    readReceiptEmail: "receipts@example.com", token);
bool b = await service.SendEmail(smtp, addresses, content, token);
bool c = await service.SendEmail(
    smtp, addresses, serializableContent, readReceipt: true,
    readReceiptEmail: "receipts@example.com", token);
bool d = await service.SendEmail(smtp, addresses, serializableContent, token);
bool e = await service.SendEmail(config, token);
bool f = await service.SendEmail(serializableConfig, token);
```

`HangfireEmailService` (`net11.0` only) has the same six overloads, but converts a `false` result from its wrapped `IEmailService` into `HangfireJobException("Failed to send email")` and otherwise returns `true`. Exceptions thrown by the wrapped service propagate.

```csharp
IEmailService wrapped = new EmailService();
IEmailService hangfireService = new HangfireEmailService(wrapped);

await hangfireService.SendEmail(smtp, addresses, content, true, "receipts@example.com", token);
await hangfireService.SendEmail(smtp, addresses, content, token);
await hangfireService.SendEmail(smtp, addresses, serializableContent, true, "receipts@example.com", token);
await hangfireService.SendEmail(smtp, addresses, serializableContent, token);
await hangfireService.SendEmail(config, token);
await hangfireService.SendEmail(serializableConfig, token);
```

Prefer `MailAttachmentBytes`, `EmailContentBytes`, and `SendEmailConfigBytes` for serialized background jobs. Do not put a live stream or secret-rich object graph in Hangfire job storage.

## Attachment ownership and methods

`IMailAttachment` exposes a mutable `AttachmentName` and `Stream? GetStream()`. Both implementations expose their overloads and disposable stream behavior:

```csharp
byte[] payload = File.ReadAllBytes("invoice.pdf");
MailAttachment fromBytes = new("invoice.pdf", payload);
MailAttachment fromStream = new("invoice.pdf", File.OpenRead("invoice.pdf"));

using (Stream? output1 = fromBytes.GetStream())
using (Stream? output2 = fromStream.GetStream())
{
    // Each call returns a new, decompressed MemoryStream. Dispose it when finished.
}

fromBytes.Dispose();
await fromStream.DisposeAsync();

MailAttachmentBytes bytesFromBytes = new("invoice.pdf", payload);
using FileStream sourceForBytes = File.OpenRead("invoice.pdf");
MailAttachmentBytes bytesFromStream = new("invoice.pdf", sourceForBytes);
using Stream? bytesOutput1 = bytesFromBytes.GetStream();
using Stream? bytesOutput2 = bytesFromStream.GetStream();
```

`MailAttachment` takes ownership of a supplied stream. Disposing the attachment disposes both its internal compressed stream and the original stream; `Dispose` and `DisposeAsync` are idempotent. Its `AttachmentStream` remains publicly settable. The stream constructor reads from the stream's current position. `EmailContent.AutoDisposeAttachments` defaults to `true`, and `SendEmail` disposes `IDisposable`/`IAsyncDisposable` attachments even after a failed send. Set it to `false` only when the caller will dispose them.

`MailAttachmentBytes` is not disposable. Its mutable `AttachmentName` and `AttachmentBytes` properties hold the public representation. Its stream constructor copies from the current stream position and does not own or dispose the source stream. Both its byte constructor and `GetStream` return independent copies; the byte constructor recognizes already-Gzip-compressed data to avoid double compression after Hangfire/Newtonsoft.Json reconstruction. Empty/null inputs produce their corresponding empty/null results, not a reusable caller-owned stream.

Each `MailAttachment` and `MailAttachmentBytes` constructor accepts an optional `CompressionLevel` (`Optimal` by default). Gzip is an internal storage detail; `GetStream()` always returns original, decompressed bytes.

`Email.AddAttachments` is public for callers that already have a `MimeKit.BodyBuilder`:

```csharp
using MimeKit;

BodyBuilder builder = new();
await Email.AddAttachments([fromBytes, bytesFromBytes], builder, zipAttachments: false);
await Email.AddAttachments([bytesFromBytes], new BodyBuilder(), zipAttachments: true);
```

Without zip, attachments receive their configured names (or `File 1`, `File 2`, ... when null). With zip, only named attachments are included and the one MIME attachment is `Files.zip`.

Email syntax can be checked independently:

```csharp
bool valid = "person@example.com".IsValidEmail();
```

`IsValidEmail` uses a bounded simple regular expression plus a nonblank check; it is not proof that a mailbox exists or that every RFC address is accepted.

## HTML email helpers

```csharp
using System.Data;
using CommonNetFuncs.Email;

string basic = HtmlEmailBuilder.BuildHtmlEmail(
    "Hello\nhttps://example.com", footer: "Regards");

using DataTable dataTable = new();
dataTable.Columns.Add("Name");
dataTable.Columns.Add("Count");
dataTable.Rows.Add("Item", 3);

string fromDataTable = HtmlEmailBuilder.BuildHtmlEmail(
    "Report", dataTable, footer: "Done", CancellationToken.None);

List<List<string>> rows =
[
    ["Name", "Count"],
    ["Item", "3"]
];
string fromLists = HtmlEmailBuilder.BuildHtmlEmail(
    "Report", rows, footer: "Done", CancellationToken.None);
```

The first two `BuildHtmlEmail` overloads—the plain body/footer overload and the `DataTable` overload—currently append the encoded body twice. The list-based overload appends it once. Preserve this behavior only if it is intentional in your application.

Lower-level extension methods and their overloads:

```csharp
string encoded = "Hello <b>world</b>\nNext\titem".StringToHtml();
string linkedDefault = "Visit https://example.com".FormatAllUrlsToHtml();
string linkedCustom = "Visit https://example.com".FormatAllUrlsToHtml("Open site");
string link = "https://example.com".CreateHtmlLink();
string customLink = "https://example.com".CreateHtmlLink("Open site");

string table1 = dataTable.CreateHtmlTable();
string table2 = dataTable.CreateHtmlTable(
    applyTableCss: false, customCss: null, CancellationToken.None);
string table3 = rows.CreateHtmlTable();
string table4 = rows.CreateHtmlTable(
    applyTableCss: true,
    customCss: "<style>table { border: 1px solid black; }</style>",
    CancellationToken.None);
```

`StringToHtml` HTML-encodes text, converts line breaks to `<br>`, and converts tabs to three `&nbsp` entities. It makes plain text safe to insert, but it intentionally does not preserve arbitrary HTML.

Do **not** treat table helpers or links as sanitizers: `DataTable` column names/cells, list headers/cells, URL strings, link text, and custom CSS are interpolated into HTML without encoding. Encode/validate untrusted values and constrain URL schemes before calling these methods; do not accept untrusted CSS. `FormatAllUrlsToHtml` recognizes `http://` and `https://` substrings only. Overlapping, duplicate, or prefix URLs preserve its replacement semantics and can yield nested/re-written links rather than a normalized HTML parse. Table generation returns an empty string for null/empty input and checks cancellation while adding rows.

`EmailConstants` exposes the implementation's `EmailRegex` and `MaxEmailLength` (320).

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
