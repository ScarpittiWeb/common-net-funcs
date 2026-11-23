using CommonNetFuncs.Email;
using FakeItEasy;
using MailKit.Net.Proxy;
using MailKit.Security;
using static CommonNetFuncs.Email.Email;
using static Xunit.TestContext;

namespace Email.Tests;

public sealed class SmtpSettingsAdvancedTests
{
	#region SmtpSettings Advanced Configuration Tests

	[Fact]
	public void SmtpSettings_WithExplicitConnectionTimeout_ShouldSetCorrectly()
	{
		// Arrange & Act
		SmtpSettings settings = new("smtp.example.com", 587, "user", "pass", TimeSpan.FromSeconds(10));

		// Assert
		settings.ConnectionTimeout.ShouldBe(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public void SmtpSettings_WithNullConnectionTimeout_ShouldUseDefault()
	{
		// Arrange & Act
		SmtpSettings settings = new("smtp.example.com", 587, "user", "pass", null);

		// Assert
		settings.ConnectionTimeout.ShouldBe(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public void SmtpSettings_DefaultConstructor_ShouldHaveDefaultConnectionTimeout()
	{
		// Arrange & Act
		SmtpSettings settings = new();

		// Assert
		settings.ConnectionTimeout.ShouldBe(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public void SmtpSettings_SetConnectionTimeout_ShouldUpdateValue()
	{
		// Arrange
		SmtpSettings settings = new()
		{
			// Act
			ConnectionTimeout = TimeSpan.FromMinutes(2)
		};

		// Assert
		settings.ConnectionTimeout.ShouldBe(TimeSpan.FromMinutes(2));
	}

	[Fact]
	public void SmtpSettings_Defaults_ShouldPreserveRetryBehavior()
	{
		SmtpSettings settings = new();

		settings.MaxAttempts.ShouldBe(8);
		settings.RetryDelay.ShouldBe(TimeSpan.FromMilliseconds(500));
	}

	[Fact]
	public void SmtpSettings_ExtendedConstructor_ShouldSetRetryBehavior()
	{
		SmtpSettings settings = new("127.0.0.1", 1, null, null, TimeSpan.FromMilliseconds(100), 3, TimeSpan.FromMilliseconds(25));

		settings.ConnectionTimeout.ShouldBe(TimeSpan.FromMilliseconds(100));
		settings.MaxAttempts.ShouldBe(3);
		settings.RetryDelay.ShouldBe(TimeSpan.FromMilliseconds(25));
	}

	[Fact]
	public void SmtpSettings_MaxAttemptsLessThanOne_ShouldThrow()
	{
		ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(() => new SmtpSettings { MaxAttempts = 0 });

		exception.ParamName.ShouldBe("value");
	}

	[Fact]
	public void SmtpSettings_NegativeRetryDelay_ShouldThrow()
	{
		ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(() => new SmtpSettings { RetryDelay = TimeSpan.FromMilliseconds(-1) });

		exception.ParamName.ShouldBe("value");
	}

	[Fact]
	public async Task SendEmail_MaxAttempts_ShouldControlConnectionAttempts()
	{
		IProxyClient proxy = A.Fake<IProxyClient>();
		A.CallTo(() => proxy.ConnectAsync(A<string>._, A<int>._, A<int>._, A<CancellationToken>._))
			.ThrowsAsync(new IOException("Expected proxy failure"));
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings("smtp.example.test", 25, connectionTimeout: TimeSpan.FromMilliseconds(100))
			{
				MaxAttempts = 3,
				RetryDelay = TimeSpan.Zero,
				ProxyClient = proxy
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = [new MailAddress("To", "to@example.com")]
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		bool result = await SendEmail(config, Current.CancellationToken);

		result.ShouldBeFalse();
		A.CallTo(() => proxy.ConnectAsync("smtp.example.test", 25, 100, A<CancellationToken>._))
			.MustHaveHappened(3, Times.Exactly);
	}

	[Fact]
	public void SmtpSettings_SetRequireTLS_ShouldUpdateValue()
	{
		// Arrange
		SmtpSettings settings = new()
		{
			// Act
			RequireTLS = true
		};

		// Assert
		settings.RequireTLS.ShouldNotBeNull();
		settings.RequireTLS.Value.ShouldBeTrue();
	}

	[Fact]
	public void SmtpSettings_SetSecureSocketOptions_ShouldUpdateValue()
	{
		// Arrange
		SmtpSettings settings = new()
		{
			// Act
			SecureSocketOptions = SecureSocketOptions.Auto
		};

		// Assert
		settings.SecureSocketOptions.ShouldBe(SecureSocketOptions.Auto);
	}

	[Fact]
	public void SmtpSettings_SetClientCertificates_ShouldUpdateValue()
	{
		// Arrange
		System.Security.Cryptography.X509Certificates.X509CertificateCollection certs = new();
		SmtpSettings settings = new()
		{
			// Act
			ClientCertificates = certs
		};

		// Assert
		settings.ClientCertificates.ShouldBe(certs);
	}

	[Fact]
	public void SmtpSettings_SetLocalDomain_ShouldUpdateValue()
	{
		// Arrange
		SmtpSettings settings = new()
		{
			// Act
			LocalDomain = "example.local"
		};

		// Assert
		settings.LocalDomain.ShouldBe("example.local");
	}

	[Fact]
	public async Task SendEmail_WithRequireTLSTrue_ShouldAttemptConnection()
	{
		// Arrange
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings
			{
				SmtpServer = "127.0.0.1",
				SmtpPort = 9990,
				SmtpUser = "user@example.com",
				SmtpPassword = "password",
				RequireTLS = true
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = new[] { new MailAddress("To", "to@example.com") }
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		// Act
		bool result = await SendEmail(config.WithFastSmtpFailure(), Current.CancellationToken);

		// Assert
		result.ShouldBeFalse(); // Will fail due to no server
	}

	[Fact]
	public async Task SendEmail_WithRequireTLSFalse_ShouldAttemptConnection()
	{
		// Arrange
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings
			{
				SmtpServer = "127.0.0.1",
				SmtpPort = 9989,
				SmtpUser = "user@example.com",
				SmtpPassword = "password",
				RequireTLS = false
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = new[] { new MailAddress("To", "to@example.com") }
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		// Act
		bool result = await SendEmail(config.WithFastSmtpFailure(), Current.CancellationToken);

		// Assert
		result.ShouldBeFalse(); // Will fail due to no server
	}

	[Fact]
	public async Task SendEmail_WithClientCertificates_ShouldAttemptConnection()
	{
		// Arrange
		System.Security.Cryptography.X509Certificates.X509CertificateCollection certs = new();
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings
			{
				SmtpServer = "127.0.0.1",
				SmtpPort = 9988,
				SmtpUser = "user@example.com",
				SmtpPassword = "password",
				ClientCertificates = certs
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = new[] { new MailAddress("To", "to@example.com") }
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		// Act
		bool result = await SendEmail(config.WithFastSmtpFailure(), Current.CancellationToken);

		// Assert
		result.ShouldBeFalse(); // Will fail due to no server
	}

	[Fact]
	public async Task SendEmail_WithLocalDomain_ShouldAttemptConnection()
	{
		// Arrange
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings
			{
				SmtpServer = "127.0.0.1",
				SmtpPort = 9987,
				SmtpUser = "user@example.com",
				SmtpPassword = "password",
				LocalDomain = "mail.example.local"
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = new[] { new MailAddress("To", "to@example.com") }
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		// Act
		bool result = await SendEmail(config.WithFastSmtpFailure(), Current.CancellationToken);

		// Assert
		result.ShouldBeFalse(); // Will fail due to no server
	}

	[Fact]
	public async Task SendEmail_WithLocalEndPoint_ShouldAttemptConnection()
	{
		// Arrange
		System.Net.IPEndPoint localEndPoint = new(System.Net.IPAddress.Loopback, 0);
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings
			{
				SmtpServer = "127.0.0.1",
				SmtpPort = 9986,
				SmtpUser = "user@example.com",
				SmtpPassword = "password",
				LocalEndPoint = localEndPoint
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = new[] { new MailAddress("To", "to@example.com") }
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		// Act
		bool result = await SendEmail(config.WithFastSmtpFailure(), Current.CancellationToken);

		// Assert
		result.ShouldBeFalse(); // Will fail due to no server
	}

	[Fact]
	public async Task SendEmail_WithExplicitSecureSocketOptionsStartTls_ShouldAttemptConnection()
	{
		// Arrange
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings
			{
				SmtpServer = "127.0.0.1",
				SmtpPort = 9985,
				SmtpUser = "user@example.com",
				SmtpPassword = "password",
				SecureSocketOptions = SecureSocketOptions.StartTls
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = new[] { new MailAddress("To", "to@example.com") }
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		// Act
		bool result = await SendEmail(config.WithFastSmtpFailure(), Current.CancellationToken);

		// Assert
		result.ShouldBeFalse(); // Will fail due to no server
	}

	[Fact]
	public async Task SendEmail_WithExplicitSecureSocketOptionsNone_ShouldAttemptConnection()
	{
		// Arrange
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings
			{
				SmtpServer = "127.0.0.1",
				SmtpPort = 9984,
				SmtpUser = "user@example.com",
				SmtpPassword = "password",
				SecureSocketOptions = SecureSocketOptions.None
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = new[] { new MailAddress("To", "to@example.com") }
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		// Act
		bool result = await SendEmail(config.WithFastSmtpFailure(), Current.CancellationToken);

		// Assert
		result.ShouldBeFalse(); // Will fail due to no server
	}

	[Fact]
	public async Task SendEmail_WithExplicitSecureSocketOptionsAuto_ShouldAttemptConnection()
	{
		// Arrange
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings
			{
				SmtpServer = "127.0.0.1",
				SmtpPort = 9983,
				SmtpUser = "user@example.com",
				SmtpPassword = "password",
				SecureSocketOptions = SecureSocketOptions.Auto
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = new[] { new MailAddress("To", "to@example.com") }
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		// Act
		bool result = await SendEmail(config.WithFastSmtpFailure(), Current.CancellationToken);

		// Assert
		result.ShouldBeFalse(); // Will fail due to no server
	}

	[Fact]
	public async Task SendEmail_WithExplicitSecureSocketOptionsSslOnConnect_ShouldAttemptConnection()
	{
		// Arrange
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings
			{
				SmtpServer = "127.0.0.1",
				SmtpPort = 9982,
				SmtpUser = "user@example.com",
				SmtpPassword = "password",
				SecureSocketOptions = SecureSocketOptions.SslOnConnect
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = new[] { new MailAddress("To", "to@example.com") }
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		// Act
		bool result = await SendEmail(config.WithFastSmtpFailure(), Current.CancellationToken);

		// Assert
		result.ShouldBeFalse(); // Will fail due to no server
	}

	[Fact]
	public async Task SendEmail_WithExplicitSecureSocketOptionsStartTlsWhenAvailable_ShouldAttemptConnection()
	{
		// Arrange
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings
			{
				SmtpServer = "127.0.0.1",
				SmtpPort = 9981,
				SmtpUser = "user@example.com",
				SmtpPassword = "password",
				SecureSocketOptions = SecureSocketOptions.StartTlsWhenAvailable
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = new[] { new MailAddress("To", "to@example.com") }
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		// Act
		bool result = await SendEmail(config.WithFastSmtpFailure(), Current.CancellationToken);

		// Assert
		result.ShouldBeFalse(); // Will fail due to no server
	}

	[Fact]
	public async Task SendEmail_WithExplicitSecureSocketOptionsForNoAuth_ShouldAttemptConnection()
	{
		// Arrange
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings
			{
				SmtpServer = "127.0.0.1",
				SmtpPort = 9980,
				SmtpUser = null,
				SmtpPassword = null,
				SecureSocketOptions = SecureSocketOptions.StartTls
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = new[] { new MailAddress("To", "to@example.com") }
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		// Act
		bool result = await SendEmail(config.WithFastSmtpFailure(), Current.CancellationToken);

		// Assert
		result.ShouldBeFalse(); // Will fail due to no server
	}

	[Fact]
	public async Task SendEmail_WithCustomConnectionTimeout_ShouldUseTimeout()
	{
		// Arrange
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings
			{
				SmtpServer = "127.0.0.1",
				SmtpPort = 9979,
				SmtpUser = "user@example.com",
				SmtpPassword = "password",
				ConnectionTimeout = TimeSpan.FromSeconds(2)
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = new[] { new MailAddress("To", "to@example.com") }
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		// Act
		bool result = await SendEmail(config.WithFastSmtpFailure(), Current.CancellationToken);

		// Assert
		result.ShouldBeFalse(); // Will fail due to no server
	}

	[Fact]
	public async Task SendEmail_WithAllAdvancedSettings_ShouldAttemptConnection()
	{
		// Arrange
		System.Security.Cryptography.X509Certificates.X509CertificateCollection certs = new();
		System.Net.IPEndPoint localEndPoint = new(System.Net.IPAddress.Loopback, 0);
		SendEmailConfig config = new()
		{
			SmtpSettings = new SmtpSettings
			{
				SmtpServer = "127.0.0.1",
				SmtpPort = 9978,
				SmtpUser = "user@example.com",
				SmtpPassword = "password",
				ConnectionTimeout = TimeSpan.FromSeconds(3),
				RequireTLS = true,
				ClientCertificates = certs,
				LocalEndPoint = localEndPoint,
				LocalDomain = "mail.example.local",
				SecureSocketOptions = SecureSocketOptions.Auto
			},
			EmailAddresses = new EmailAddresses
			{
				FromAddress = new MailAddress("Sender", "sender@example.com"),
				ToAddresses = new[] { new MailAddress("To", "to@example.com") }
			},
			EmailContent = new EmailContent("Subject", "Body")
		};

		// Act
		bool result = await SendEmail(config.WithFastSmtpFailure(), Current.CancellationToken);

		// Assert
		result.ShouldBeFalse(); // Will fail due to no server
	}

	#endregion

	#region IsValidEmail Exception Tests

	[Fact]
	public void IsValidEmail_WithExtremelyLongEmail_ShouldReturnValidationResult()
	{
		// Arrange - Create a very long email that is technically valid
		string veryLongEmail = new string('a', 5000) + "@" + new string('b', 5000) + ".com";

		// Act
		bool result = veryLongEmail.IsValidEmail();

		// Assert - Should complete without throwing (the result will be based on the regex validation)
		// This test ensures the exception handling path exists even if not hit
		result.ShouldBeTrue();
	}

	[Fact]
	public void IsValidEmail_WithComplexPattern_ShouldHandleGracefully()
	{
		// Arrange - Email that matches the regex pattern
		string complexEmail = new string('a', 500) + "@" + new string('b', 500) + "." + new string('c', 500);

		// Act
		bool result = complexEmail.IsValidEmail();

		// Assert - Should complete without throwing (the result will be based on the regex validation)
		// This test ensures the exception handling path exists even if not hit
		result.ShouldBeTrue();
	}

	#endregion
}
