using AutoFixture;
using AutoFixture.AutoFakeItEasy;
using CommonNetFuncs.Web.Ftp;
using FakeItEasy;
using FakeItEasy.Core;
using Renci.SshNet;
using Renci.SshNet.Common;
using System.Reflection;
using System.Text;

namespace Web.Ftp.Tests;

/// <summary>
/// Tests for SshFtp extension methods.
/// Note: Many methods cannot be fully tested with mocks because SftpClient methods are non-virtual.
/// These tests focus on null checks and basic validation logic that can be tested without actual SFTP connections.
/// </summary>
public class SshFtpTests
{
	// SftpClient.IsConnected is the only virtual member on SftpClient, so it's the only thing FakeItEasy can override.
	// Faking it to true (without a real network connection) lets these tests exercise the "client believes it's connected"
	// branch of each extension method; the underlying real SftpClient methods then throw their own SshConnectionException
	// (message "Client not connected.") because the internal SFTP session was never actually established.
	private static SftpClient CreateConnectedFakeClient()
	{
		IFixture fixture = new Fixture().Customize(new AutoFakeItEasyCustomization());
		FileTransferConnection connection = fixture.Create<FileTransferConnection>();
		SftpClient sftpClient = A.Fake<SftpClient>(options => options.WithArgumentsForConstructor(() =>
			new SftpClient(connection.HostName, connection.Port, connection.UserName, connection.Password)));
		A.CallTo(() => sftpClient.IsConnected).Returns(true);
		return sftpClient;
	}

	#region GetHostName Tests

	[Fact]
	public void GetHostName_ShouldReturnHostName()
	{
		// Arrange
		FileTransferConnection connection = new()
		{
			HostName = "test.server.com",
			UserName = "TestUser",
			Password = "TestPass",
			Port = 22
		};

		// Act
		string result = connection.GetHostName();

		// Assert
		result.ShouldBe("test.server.com");
	}

	[Fact]
	public void GetHostName_WithDifferentHostNames_ShouldReturnCorrectValue()
	{
		// Arrange
		FileTransferConnection connection1 = new() { HostName = "server1.com", UserName = "user", Password = "pass", Port = 22 };
		FileTransferConnection connection2 = new() { HostName = "ftp.example.org", UserName = "user", Password = "pass", Port = 22 };

		// Act & Assert
		connection1.GetHostName().ShouldBe("server1.com");
		connection2.GetHostName().ShouldBe("ftp.example.org");
	}

	#endregion

	#region IsConnected Tests

	[Fact]
	public void IsConnected_WhenClientIsNull_ShouldReturnFalse()
	{
		// Arrange
		SftpClient? client = null;

		// Act
		bool result = client.IsConnected();

		// Assert
		result.ShouldBeFalse();
	}

	#endregion

	#region DisconnectClient Tests

	[Fact]
	public void DisconnectClient_WhenClientIsNull_ShouldReturnFalse()
	{
		// Arrange
		SftpClient? client = null;

		// Act
		bool result = client.DisconnectClient();

		// Assert
		result.ShouldBeFalse();
	}

	#endregion

	#region DirectoryOrFileExists Tests

	[Fact]
	public void DirectoryOrFileExists_WhenClientIsNull_ShouldThrowSshConnectionException()
	{
		// Arrange
		SftpClient? client = null;
		const string path = "/test/path";

		// Act & Assert
		Should.Throw<SshConnectionException>(() => client.DirectoryOrFileExists(path)).Message.ShouldBe("SFTP client is not connected.");
	}

	[Theory]
	[InlineData("/test/path")]
	[InlineData("/another/directory")]
	[InlineData("/file.txt")]
	public void DirectoryOrFileExists_WhenClientIsNull_ShouldThrowForAnyPath(string path)
	{
		// Arrange
		SftpClient? client = null;

		// Act & Assert
		Should.Throw<SshConnectionException>(() => client.DirectoryOrFileExists(path)).Message.ShouldBe("SFTP client is not connected.");
	}

	#endregion

	#region DirectoryOrFileExistsAsync Tests

	[Fact]
	public async Task DirectoryOrFileExistsAsync_WhenClientIsNull_ShouldThrowSshConnectionException()
	{
		// Arrange
		SftpClient? client = null;
		const string path = "/test/path";

		// Act & Assert
		SshConnectionException exception = await Should.ThrowAsync<SshConnectionException>(async () => await client.DirectoryOrFileExistsAsync(path));
		exception.Message.ShouldBe("SFTP client is not connected.");
	}

	[Theory]
	[InlineData("/test/path")]
	[InlineData("/data/file.xml")]
	[InlineData("/home/user/docs")]
	public async Task DirectoryOrFileExistsAsync_WhenClientIsNull_ShouldThrowForAnyPath(string path)
	{
		// Arrange
		SftpClient? client = null;

		// Act & Assert
		SshConnectionException exception = await Should.ThrowAsync<SshConnectionException>(async () => await client.DirectoryOrFileExistsAsync(path));
		exception.Message.ShouldBe("SFTP client is not connected.");
	}

	#endregion

	#region GetFileList Tests

	[Fact]
	public void GetFileList_WhenClientIsNull_ShouldThrowSshConnectionException()
	{
		// Arrange
		SftpClient? client = null;
		const string path = "/test/path";

		// Act & Assert
		Should.Throw<SshConnectionException>(() => client.GetFileList(path)).Message.ShouldBe("SFTP client is not connected.");
	}

	[Theory]
	[InlineData("/test/path", "*")]
	[InlineData("/data", "txt")]
	[InlineData("/files", "csv")]
	public void GetFileList_WhenClientIsNull_ShouldThrowForAnyPathAndExtension(string path, string extension)
	{
		// Arrange
		SftpClient? client = null;

		// Act & Assert
		Should.Throw<SshConnectionException>(() => client.GetFileList(path, extension)).Message.ShouldBe("SFTP client is not connected.");
	}

	#endregion

	#region GetFileListAsync Tests

	[Fact]
	public async Task GetFileListAsync_WhenClientIsNull_ShouldThrowSshConnectionException()
	{
		// Arrange
		SftpClient? client = null;
		const string path = "/test/path";

		// Act & Assert
		await Should.ThrowAsync<SshConnectionException>(async () =>
		{
#pragma warning disable S108 // Nested blocks of code should not be left empty
			await foreach (string _ in client.GetFileListAsync(path)) { }
#pragma warning restore S108 // Nested blocks of code should not be left empty
		});
	}

	[Theory]
	[InlineData("/test", "*")]
	[InlineData("/data", "log")]
	[InlineData("/files", "json")]
	public async Task GetFileListAsync_WhenClientIsNull_ShouldThrowForAnyPathAndExtension(string path, string extension)
	{
		// Arrange
		SftpClient? client = null;

		// Act & Assert
		await Should.ThrowAsync<SshConnectionException>(async () =>
		{
#pragma warning disable S108 // Nested blocks of code should not be left empty
			await foreach (string _ in client.GetFileListAsync(path, extension)) { }
#pragma warning restore S108 // Nested blocks of code should not be left empty
		});
	}

	#endregion

	#region GetDataFromCsvAsync Tests

	[Fact]
	public async Task GetDataFromCsvAsync_WhenClientIsNull_ShouldThrowSshConnectionException()
	{
		// Arrange
		SftpClient? client = null;
		const string path = "/test/file.csv";

		// Act & Assert
		await Should.ThrowAsync<SshConnectionException>(async () => await client.GetDataFromCsvAsync<TestCsvModel>(path));
	}

	[Theory]
	[InlineData("/data/test.csv")]
	[InlineData("/files/data.csv")]
	[InlineData("/export.csv")]
	public async Task GetDataFromCsvAsync_WhenClientIsNull_ShouldThrowForAnyCsvPath(string path)
	{
		// Arrange
		SftpClient? client = null;

		// Act & Assert
		await Should.ThrowAsync<SshConnectionException>(async () => await client.GetDataFromCsvAsync<TestCsvModel>(path));
	}

	#endregion

	#region GetDataFromCsvAsyncEnumerable Tests

	[Fact]
	public async Task GetDataFromCsvAsyncEnumerable_WhenClientIsNull_ShouldThrowSshConnectionException()
	{
		// Arrange
		SftpClient? client = null;
		const string path = "/test/file.csv";

		// Act & Assert
		await Should.ThrowAsync<SshConnectionException>(async () =>
		{
#pragma warning disable S108 // Nested blocks of code should not be left empty
			await foreach (TestCsvModel _ in client.GetDataFromCsvAsyncEnumerable<TestCsvModel>(path)) { }
#pragma warning restore S108 // Nested blocks of code should not be left empty
		});
	}

	[Theory]
	[InlineData("/data/records.csv")]
	[InlineData("/output.csv")]
	public async Task GetDataFromCsvAsyncEnumerable_WhenClientIsNull_ShouldThrowForAnyCsvPath(string path)
	{
		// Arrange
		SftpClient? client = null;

		// Act & Assert
		await Should.ThrowAsync<SshConnectionException>(async () =>
		{
#pragma warning disable S108 // Nested blocks of code should not be left empty
			await foreach (TestCsvModel _ in client.GetDataFromCsvAsyncEnumerable<TestCsvModel>(path)) { }
#pragma warning restore S108 // Nested blocks of code should not be left empty
		});
	}

	#endregion

	#region GetDataFromCsvCopyAsyncEnumerable Tests

	[Fact]
	public async Task GetDataFromCsvEnumerable_WhenClientIsNull_ShouldThrowSshConnectionException()
	{
		// Arrange
		SftpClient? client = null;
		const string path = "/test/file.csv";

		// Act & Assert
		Should.Throw<SshConnectionException>(() =>
		{
#pragma warning disable S108 // Nested blocks of code should not be left empty
			foreach (TestCsvModel _ in client.GetDataFromCsvEnumerable<TestCsvModel>(path)) { }
#pragma warning restore S108 // Nested blocks of code should not be left empty
		});
	}

	[Theory]
	[InlineData("/backup/data.csv")]
	[InlineData("/tmp/export.csv")]
	public void GetDataFromCsvEnumerable_WhenClientIsNull_ShouldThrowForAnyCsvPath(string path)
	{
		// Arrange
		SftpClient? client = null;

		// Act & Assert
		Should.Throw<SshConnectionException>(() =>
		{
#pragma warning disable S108 // Nested blocks of code should not be left empty
			foreach (TestCsvModel _ in client.GetDataFromCsvEnumerable<TestCsvModel>(path)) { }
#pragma warning restore S108 // Nested blocks of code should not be left empty
		});
	}

	#endregion

	#region GetDataFromCsv Tests

	[Fact]
	public void GetDataFromCsv_WhenClientIsNull_ShouldThrowSshConnectionException()
	{
		// Arrange
		SftpClient? client = null;
		const string path = "/test/file.csv";

		// Act & Assert
		Should.Throw<SshConnectionException>(() => client.GetDataFromCsv<TestCsvModel>(path));
	}

	[Theory]
	[InlineData("/uploads/data.csv")]
	[InlineData("/share/report.csv")]
	public void GetDataFromCsv_WhenClientIsNull_ShouldThrowForAnyCsvPath(string path)
	{
		// Arrange
		SftpClient? client = null;

		// Act & Assert
		Should.Throw<SshConnectionException>(() => client.GetDataFromCsv<TestCsvModel>(path));
	}

	#endregion

	#region DeleteSftpFile Tests

	[Fact]
	public void DeleteSftpFile_WhenClientIsNull_ShouldThrowSshConnectionException()
	{
		// Arrange
		SftpClient? client = null;
		const string path = "/test/file.txt";

		// Act & Assert
		Should.Throw<SshConnectionException>(() => client.DeleteSftpFile(path)).Message.ShouldBe("SFTP client is not connected.");
	}

	[Theory]
	[InlineData("/tmp/file.log")]
	[InlineData("/data/old.dat")]
	[InlineData("/delete-me.txt")]
	public void DeleteSftpFile_WhenClientIsNull_ShouldThrowForAnyPath(string path)
	{
		// Arrange
		SftpClient? client = null;

		// Act & Assert
		Should.Throw<SshConnectionException>(() => client.DeleteSftpFile(path)).Message.ShouldBe("SFTP client is not connected.");
	}

	#endregion

	#region DeleteFileAsync Tests

	[Fact]
	public async Task DeleteFileAsync_WhenClientIsNull_ShouldThrowSshConnectionException()
	{
		// Arrange
		SftpClient? client = null;
		const string path = "/test/file.txt";

		// Act & Assert
		SshConnectionException exception = await Should.ThrowAsync<SshConnectionException>(async () => await client.DeleteFileAsync(path));
		exception.Message.ShouldBe("SFTP client is not connected.");
	}

	[Theory]
	[InlineData("/archive/old.zip")]
	[InlineData("/temp/cache.tmp")]
	[InlineData("/remove.dat")]
	public async Task DeleteFileAsync_WhenClientIsNull_ShouldThrowForAnyPath(string path)
	{
		// Arrange
		SftpClient? client = null;

		// Act & Assert
		SshConnectionException exception = await Should.ThrowAsync<SshConnectionException>(async () => await client.DeleteFileAsync(path));
		exception.Message.ShouldBe("SFTP client is not connected.");
	}

	#endregion

	#region Connected (faked) client tests

	// These exercise the "IsConnected() == true" branch of each guard clause, which is unreachable when the client is null.
	// Since the fake client was never actually connected to a real server, the underlying real SftpClient calls then throw
	// their own SshConnectionException("Client not connected.") once they reach the SFTP session null-check.

	[Fact]
	public void DisconnectClient_WhenConnected_ShouldAttemptDisconnectAndReturnConnectionState()
	{
		SftpClient client = CreateConnectedFakeClient();

		// The fake reports IsConnected == true, so DisconnectClient calls the real (non-virtual) Disconnect(); since there's
		// no actual session, SSH.NET no-ops rather than throwing, and the final IsConnected() check (still faked true) returns true.
		bool result = client.DisconnectClient();
		result.ShouldBeTrue();
	}

	[Fact]
	public void DirectoryOrFileExists_WhenConnected_ShouldThrowUnderlyingConnectionException()
	{
		SftpClient client = CreateConnectedFakeClient();
		Should.Throw<SshConnectionException>(() => client.DirectoryOrFileExists("/test/path")).Message.ShouldBe("Client not connected.");
	}

	[Fact]
	public async Task DirectoryOrFileExistsAsync_WhenConnected_ShouldThrowUnderlyingConnectionException()
	{
		SftpClient client = CreateConnectedFakeClient();
		SshConnectionException exception = await Should.ThrowAsync<SshConnectionException>(async () => await client.DirectoryOrFileExistsAsync("/test/path"));
		exception.Message.ShouldBe("Client not connected.");
	}

	[Fact]
	public void GetFileList_WhenConnected_ShouldThrowUnderlyingConnectionException()
	{
		SftpClient client = CreateConnectedFakeClient();
		Should.Throw<SshConnectionException>(() => client.GetFileList("/test/path")).Message.ShouldBe("Client not connected.");
	}

	[Fact]
	public async Task GetFileListAsync_WhenConnected_ShouldThrowUnderlyingConnectionException()
	{
		SftpClient client = CreateConnectedFakeClient();
		await Should.ThrowAsync<SshConnectionException>(async () =>
		{
#pragma warning disable S108 // Nested blocks of code should not be left empty
			await foreach (string _ in client.GetFileListAsync("/test/path")) { }
#pragma warning restore S108 // Nested blocks of code should not be left empty
		});
	}

	[Fact]
	public async Task GetDataFromCsvAsync_WhenConnected_ShouldThrowUnderlyingConnectionException()
	{
		SftpClient client = CreateConnectedFakeClient();
		await Should.ThrowAsync<SshConnectionException>(async () => await client.GetDataFromCsvAsync<TestCsvModel>("/test/file.csv"));
	}

	[Fact]
	public async Task GetDataFromCsvAsyncEnumerable_WhenConnected_ShouldThrowUnderlyingConnectionException()
	{
		SftpClient client = CreateConnectedFakeClient();
		await Should.ThrowAsync<SshConnectionException>(async () =>
		{
#pragma warning disable S108 // Nested blocks of code should not be left empty
			await foreach (TestCsvModel _ in client.GetDataFromCsvAsyncEnumerable<TestCsvModel>("/test/file.csv")) { }
#pragma warning restore S108 // Nested blocks of code should not be left empty
		});
	}

	[Fact]
	public void GetDataFromCsvEnumerable_WhenConnected_ShouldThrowUnderlyingConnectionException()
	{
		SftpClient client = CreateConnectedFakeClient();
		Should.Throw<SshConnectionException>(() =>
		{
#pragma warning disable S108 // Nested blocks of code should not be left empty
			foreach (TestCsvModel _ in client.GetDataFromCsvEnumerable<TestCsvModel>("/test/file.csv")) { }
#pragma warning restore S108 // Nested blocks of code should not be left empty
		});
	}

	[Fact]
	public void GetDataFromCsv_WhenConnected_ShouldThrowUnderlyingConnectionException()
	{
		SftpClient client = CreateConnectedFakeClient();
		Should.Throw<SshConnectionException>(() => client.GetDataFromCsv<TestCsvModel>("/test/file.csv"));
	}

	[Fact]
	public void DeleteSftpFile_WhenConnected_ShouldThrowUnderlyingConnectionException()
	{
		SftpClient client = CreateConnectedFakeClient();
		Should.Throw<SshConnectionException>(() => client.DeleteSftpFile("/test/file.txt")).Message.ShouldBe("Client not connected.");
	}

	[Fact]
	public async Task DeleteFileAsync_WhenConnected_ShouldThrowUnderlyingConnectionException()
	{
		SftpClient client = CreateConnectedFakeClient();
		SshConnectionException exception = await Should.ThrowAsync<SshConnectionException>(async () => await client.DeleteFileAsync("/test/file.txt"));
		exception.Message.ShouldBe("Client not connected.");
	}

	#endregion

	#region Test Models

#pragma warning disable S1144 // Unused private types or members should be removed
	private class TestCsvModel
	{
		public string Name { get; set; } = string.Empty;
		public int Age { get; set; }
#pragma warning restore S1144 // Unused private types or members should be removed
	}

	#endregion
}

public sealed class SshFtpBehaviorTests
{
	private static readonly Type SessionType = typeof(SftpClient).Assembly.GetType("Renci.SshNet.Sftp.ISftpSession", true)!;
	private static readonly Type AttributesType = typeof(SftpClient).Assembly.GetType("Renci.SshNet.Sftp.SftpFileAttributes", true)!;
	private static readonly FieldInfo SessionField = typeof(SftpClient).GetField("_sftpSession", BindingFlags.Instance | BindingFlags.NonPublic)!;













	private static (SftpClient Client, object Session) CreateSessionBackedClient()
	{
		MethodInfo fakeMethod = typeof(A).GetMethods(BindingFlags.Public | BindingFlags.Static)
			.Single(x => x.Name == "Fake" && x.IsGenericMethodDefinition && x.GetParameters().Length == 0);
		object session = fakeMethod.MakeGenericMethod(SessionType).Invoke(null, null)!;
		Configure(session, "get_WorkingDirectory", _ => "/");
		Configure(session, "GetCanonicalPath", call => call.Arguments.Single());
		Configure(session, "GetCanonicalPathAsync", call =>
			CompletedTask(call.Arguments[0], typeof(string)));
		SftpClient client = A.Fake<SftpClient>(options =>
			options.WithArgumentsForConstructor(() => new SftpClient("host", 22, "user", "pass")));
		A.CallTo(() => client.IsConnected).Returns(true);
		SessionField.SetValue(client, session);
		return (client, session);
	}

	private static object? CreateAttributes(bool directory = false, long size = 0)
	{
		ConstructorInfo constructor = AttributesType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
		uint permissions = directory ? 0x4000u : 0x8000u;
		return constructor.Invoke([DateTime.UtcNow, DateTime.UtcNow, size, 0, 0, permissions, new Dictionary<string, string>()]);
	}

	private static object CreateEntries(IEnumerable<(string Name, bool Directory)> values)
	{
		Type pairType = typeof(KeyValuePair<,>).MakeGenericType(typeof(string), AttributesType);
		Array entries = Array.CreateInstance(pairType, values.Count());
		int index = 0;
		foreach ((string name, bool directory) in values)
		{
			entries.SetValue(Activator.CreateInstance(pairType, name, CreateAttributes(directory)), index++);
		}
		return entries;
	}

	private static void Configure(object session, string methodName, Func<IFakeObjectCall, object?> handler)
	{
		Fake.GetFakeManager(session).AddRuleFirst(new SessionRule(methodName, handler));
	}

	private static IEnumerable<IFakeObjectCall> GetCalls(object session, string methodName)
	{
		return Fake.GetCalls(session).Where(x => x.Method.Name == methodName);
	}

	private static object CompletedTask(object? value, Type valueType)
	{
		MethodInfo fromResult = typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
			.Single(x => x.Name == "FromResult" && x.IsGenericMethodDefinition);
		return fromResult.MakeGenericMethod(valueType).Invoke(null, [value])!;
	}

	private sealed class SessionRule(string methodName, Func<IFakeObjectCall, object?> handler) : IFakeObjectCallRule
	{
		public int? NumberOfTimesToCall => null;

		public bool IsApplicableTo(IFakeObjectCall fakeObjectCall) => fakeObjectCall.Method.Name == methodName;

		public void Apply(IInterceptedFakeObjectCall fakeObjectCall)
		{
			fakeObjectCall.SetReturnValue(handler(fakeObjectCall));
		}
	}

	private sealed class CsvRow
	{
		public string Value { get; set; } = string.Empty;
	}

	[Fact]
	public void DirectoryOrFileExists_WithConnectedSession_UsesRequestedPath()
	{
		(SftpClient client, object session) = CreateSessionBackedClient();
		Configure(session, "RequestLStat", _ => CreateAttributes());
		Configure(session, "RequestStat", _ => CreateAttributes());

		bool result = client.DirectoryOrFileExists("/remote/file.txt");

		result.ShouldBeTrue();
		GetCalls(session, "RequestLStat").Single().Arguments.Single().ShouldBe("/remote/file.txt");
	}

	[Fact]
	public async Task DirectoryOrFileExistsAsync_WithExistingPath_ReturnsTrueAndUsesNoCancellation()
	{
		(SftpClient client, object session) = CreateSessionBackedClient();
		Configure(session, "RequestLStatAsync", _ => CompletedTask(CreateAttributes(), AttributesType));

		bool result = await client.DirectoryOrFileExistsAsync("/missing");

		result.ShouldBeTrue();
		((CancellationToken)GetCalls(session, "RequestLStatAsync").Single().Arguments[1]!).ShouldBe(CancellationToken.None);
	}

	[Fact]
	public void GetFileList_WhenPathIsMissing_ThrowsArgumentException()
	{
		(SftpClient client, object session) = CreateSessionBackedClient();
		Configure(session, "RequestLStat", _ => throw new SftpPathNotFoundException("missing"));

		ArgumentException exception = Should.Throw<ArgumentException>(() => client.GetFileList("/missing"));

		exception.ParamName.ShouldBe("path");
		exception.Message.ShouldContain("/missing");
	}

	[Fact]
	public void GetDataFromCsv_WhenExtensionIsNotCsv_RejectsBeforeOpening()
	{
		(SftpClient client, object session) = CreateSessionBackedClient();

		ArgumentException exception = Should.Throw<ArgumentException>(() => client.GetDataFromCsv<CsvRow>("/file.txt").ToList());

		exception.ParamName.ShouldBe("remoteFilePath");
		GetCalls(session, "RequestOpen").ShouldBeEmpty();
	}

	[Fact]
	public void GetDataFromCsv_ReturnsLazySequenceAfterOwningStreamIsDisposed()
	{
		(SftpClient client, object session) = CreateSessionBackedClient();
		byte[] csv = Encoding.UTF8.GetBytes("Value\nhello\nworld\n");
		Configure(session, "RequestLStat", _ => CreateAttributes());
		Configure(session, "RequestOpen", _ => "handle"u8.ToArray());
		Configure(session, "RequestFStat", _ => CreateAttributes(size: csv.Length));
		Configure(session, "RequestRead", call =>
		{
			int offset = checked((int)(ulong)call.Arguments[1]!);
			int length = Math.Min(checked((int)(uint)call.Arguments[2]!), Math.Max(0, csv.Length - offset));
			return length == 0 ? [] : csv.AsSpan(offset, length).ToArray();
		});

		IEnumerable<CsvRow> rows = client.GetDataFromCsv<CsvRow>("/file.csv");

		Should.Throw<ArgumentException>(() => rows.ToList()).Message.ShouldBe("Stream was not readable.");
		GetCalls(session, "RequestOpen").ShouldHaveSingleItem();
	}

	[Fact]
	public async Task GetDataFromCsvAsync_WhenExtensionIsNotCsv_RejectsBeforeOpening()
	{
		(SftpClient client, object session) = CreateSessionBackedClient();

		ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
			() => client.GetDataFromCsvAsync<CsvRow>("/file.txt"));

		exception.ParamName.ShouldBe("remoteFilePath");
		GetCalls(session, "RequestOpenAsync").ShouldBeEmpty();
	}

	[Fact]
	public async Task GetDataFromCsvAsyncEnumerable_WhenExtensionIsNotCsv_RejectsDuringEnumeration()
	{
		(SftpClient client, object session) = CreateSessionBackedClient();

		await Should.ThrowAsync<ArgumentException>(async () =>
		{
			await foreach (CsvRow _ in client.GetDataFromCsvAsyncEnumerable<CsvRow>("/file.txt")) { }
		});
		GetCalls(session, "RequestOpenAsync").ShouldBeEmpty();
	}

	[Fact]
	public void GetDataFromCsvEnumerable_WhenExtensionIsNotCsv_RejectsDuringEnumeration()
	{
		(SftpClient client, object session) = CreateSessionBackedClient();

		Should.Throw<ArgumentException>(() =>
		{
			foreach (CsvRow _ in client.GetDataFromCsvEnumerable<CsvRow>("/file.txt")) { }
		});
		GetCalls(session, "RequestOpen").ShouldBeEmpty();
	}

	[Fact]
	public void DeleteSftpFile_WhenFileDisappears_ReturnsTrueAndUsesRemove()
	{
		(SftpClient client, object session) = CreateSessionBackedClient();
		bool exists = true;
		Configure(session, "RequestLStat", _ => exists ? CreateAttributes() : throw new SftpPathNotFoundException("missing"));
		Configure(session, "RequestStat", _ => exists ? CreateAttributes() : throw new SftpPathNotFoundException("missing"));
		Configure(session, "RequestRemove", _ =>
		{
			exists = false;
			return null;
		});

		bool result = client.DeleteSftpFile("/remove.txt");

		result.ShouldBeTrue();
		GetCalls(session, "RequestRemove").Single().Arguments.Single().ShouldBe("/remove.txt");
	}

	[Fact]
	public void DeleteSftpFile_WhenFileWasAlreadyMissing_DoesNotRemove()
	{
		(SftpClient client, object session) = CreateSessionBackedClient();
		Configure(session, "RequestLStat", _ => throw new SftpPathNotFoundException("missing"));
		Configure(session, "RequestStat", _ => throw new SftpPathNotFoundException("missing"));

		client.DeleteSftpFile("/missing").ShouldBeTrue();

		GetCalls(session, "RequestRemove").ShouldBeEmpty();
	}

	[Fact]
	public async Task DeleteFileAsync_WhenFileDisappears_ReturnsTrueAndUsesCancellationDefault()
	{
		(SftpClient client, object session) = CreateSessionBackedClient();
		bool exists = true;
		Configure(session, "RequestLStatAsync", _ => exists
			? CompletedTask(CreateAttributes(), AttributesType)
			: throw new SftpPathNotFoundException("missing"));
		Configure(session, "RequestStat", _ => exists ? CreateAttributes() : throw new SftpPathNotFoundException("missing"));
		Configure(session, "RequestRemoveAsync", _ =>
		{
			exists = false;
			return Task.CompletedTask;
		});

		bool result = await client.DeleteFileAsync("/remove.txt");

		result.ShouldBeTrue();
		GetCalls(session, "RequestRemoveAsync").Single().Arguments[1].ShouldBe(CancellationToken.None);
	}

}
