using CommonNetFuncs.Email;

namespace Email.Tests;

internal static class FastSmtpFailureExtensions
{
	public static SmtpSettings WithFastSmtpFailure(this SmtpSettings settings)
	{
		// Use a local address and port 1 so DNS and routing cannot make the failure path
		// depend on external services. Preserve an explicitly-null server because tests
		// separately cover that input.
		if (settings.SmtpServer != null)
		{
			settings.SmtpServer = "127.0.0.1";
			settings.SmtpPort = 1;
		}

		settings.ConnectionTimeout = TimeSpan.FromMilliseconds(100);
		settings.MaxAttempts = 2;
		settings.RetryDelay = TimeSpan.Zero;
		return settings;
	}

	public static SendEmailConfig WithFastSmtpFailure(this SendEmailConfig config)
	{
		config.SmtpSettings.WithFastSmtpFailure();
		return config;
	}

	public static SendEmailConfigBytes WithFastSmtpFailure(this SendEmailConfigBytes config)
	{
		config.SmtpSettings.WithFastSmtpFailure();
		return config;
	}
}
