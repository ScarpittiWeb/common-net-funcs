using System;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;

namespace BenchmarkSuite;

internal static class Program
{
	// This setup allow running specific benchmarks from command line like this:
	// dotnet run -c Release -- --job short --filter *BenchmarkClass1*
	static void Main(string[] args)
	{
		// dotnet run -c Release -- --verify : checks that each Perf* "Optimized" variant matches its "Original" on edge-case inputs
		if (args.Length > 0 && args[0] == "--verify")
		{
			RunVerifiers();
			return;
		}

		BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, CreateConfig());
	}

	private static void RunVerifiers()
	{
		(string Name, Action Run)[] verifiers =
		[
			("Strings", PsVerify.Run),
			("Core", PcVerify.Run),
			("Office", PoVerify.Run),
			("Data", PdVerify.Run),
			("Web", PwVerify.Run),
			("Misc", PmVerify.Run),
		];

		foreach ((string name, Action run) in verifiers)
		{
			try
			{
				run();
				Console.WriteLine($"VERIFY {name}: PASS");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"VERIFY {name}: FAIL -> {ex}");
			}
		}
	}

	private static IConfig CreateConfig()
	{
		IConfig config = DefaultConfig.Instance;
		int major = Environment.Version.Major;

		// BenchmarkDotNet 0.15.8 has no RuntimeMoniker for .NET 11+, so anything calling RuntimeMoniker.GetRuntimeVersion() throws
		// "GetRuntimeVersion not implemented for NotRecognized" (CsProjCoreToolchain's SDK validator and the parallel-build SDK check).
		// Reuse the standard out-of-process csproj generator/builder/executor for the host TFM but skip that validator, and build
		// sequentially to bypass the parallel-build check. Self-disables once the BenchmarkDotNet package knows the running runtime.
		if (Enum.TryParse<RuntimeMoniker>($"Net{major}_0", out _))
		{
			return config;
		}

		IToolchain csProjToolchain = CsProjCoreToolchain.From(new NetCoreAppSettings($"net{major}.0", null, $".NET {major}.0"));
		Toolchain hostToolchain = new(csProjToolchain.Name, csProjToolchain.Generator, csProjToolchain.Builder, csProjToolchain.Executor);
		return config
			.AddJob(Job.Default.WithToolchain(hostToolchain).AsMutator())
			.WithOption(ConfigOptions.DisableParallelBuild, true);
	}
}
