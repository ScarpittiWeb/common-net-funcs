using CommonNetFuncs.Web.Ftp;
using FakeItEasy;
using FakeItEasy.Core;
using Renci.SshNet;
using System.Reflection;

namespace Web.Ftp.Tests;

public sealed class FileTransferConnectionBehaviorTests
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
	public void FileTransferConnection_Properties_RetainAllConfiguredValues()
	{
		FileTransferConnection connection = new()
		{
			HostName = "host",
			UserName = "user",
			Password = "pass",
			Port = 2222,
			BufferSize = 8192,
			ConnectionProtocol = "SFTP"
		};

		connection.HostName.ShouldBe("host");
		connection.ConnectionProtocol.ShouldBe("SFTP");
		connection.BufferSize.ShouldBe((uint)8192);
	}

}
