using CommonNetFuncs.Word.OpenXml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using System.Text.RegularExpressions;
using xRetry.v3;

namespace Word.OpenXml.Tests;

public sealed class ChangeUrlTests : IDisposable
{
	private readonly string testDocPath;
	private readonly string tempDocPath;
	private readonly FileStream? tempFileStream;

	private bool disposed;

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	private void Dispose(bool disposing)
	{
		if (!disposed)
		{
			if (disposing)
			{
				tempFileStream?.Dispose();
			}
			disposed = true;
		}
		if (File.Exists(tempDocPath))
		{
			File.Delete(tempDocPath);
		}
	}

	~ChangeUrlTests()
	{
		Dispose(false);
	}

	public ChangeUrlTests()
	{
		// Set up test paths
		testDocPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "TestDocument.docx");
		tempDocPath = Path.GetTempFileName();

		// Copy test doc to temp location for each test
		File.Copy(testDocPath, tempDocPath, true);
		tempFileStream = new FileStream(tempDocPath, FileMode.Open, FileAccess.ReadWrite);
	}

	private static List<HyperlinkRelationship> GetHyperlinks(Stream docStream)
	{
		using WordprocessingDocument doc = WordprocessingDocument.Open(docStream, false);
		return doc.MainDocumentPart?.HyperlinkRelationships.ToList() ?? new List<HyperlinkRelationship>();
	}

	[RetryTheory(3)]
	[InlineData("http://TestUrl/1", "http://NewUrl/1", true)]
	[InlineData("http://TestUrl/2", "http://NewUrl/2", false)]
	public void ChangeUrlsInWordDoc_SingleUrl_ReplacesCorrectly(string urlToReplace, string newUrl, bool replaceAll)
	{
		// Act
		bool result = CommonNetFuncs.Word.OpenXml.ChangeUrls.ChangeUrlsInWordDoc(tempFileStream!, newUrl, urlToReplace, replaceAll);
		tempFileStream!.Position = 0;

		// Assert
		result.ShouldBeTrue();
		List<HyperlinkRelationship> hyperlinks = GetHyperlinks(tempFileStream);

		// Verify the URL was replaced
		hyperlinks.Count(h => h.Uri.ToString().Equals(urlToReplace, StringComparison.InvariantCultureIgnoreCase))
				.ShouldBe(replaceAll ? 0 : 1);

		// Verify the new URL exists
		hyperlinks.Count(h => h.Uri.ToString().Equals(newUrl, StringComparison.InvariantCultureIgnoreCase))
				.ShouldBe(replaceAll ? 2 : 1);

		// Control URLs should remain unchanged
		hyperlinks.ShouldContain(h => h.Uri.ToString().Contains("google.com"));
		hyperlinks.ShouldContain(h => h.Uri.ToString().Contains("xkcd.com"));
		hyperlinks.ShouldContain(h => h.Uri.ToString().Contains("github.com"));
	}

	[RetryFact(3)]
	public void ChangeUrlsInWordDoc_Dictionary_ReplacesMultipleUrls()
	{
		// Arrange
		Dictionary<string, string> urlsToUpdate = new()
			{
				{ "http://TestUrl/1", "http://NewUrl/1" },
				{ "http://TestUrl/2", "http://NewUrl/2" },
				{ "http://TestUrl/3", "http://NewUrl/3" }
			};

		// Act
		bool result = CommonNetFuncs.Word.OpenXml.ChangeUrls.ChangeUrlsInWordDoc(tempFileStream!, urlsToUpdate);
		tempFileStream!.Position = 0;

		// Assert
		result.ShouldBeTrue();
		IEnumerable<HyperlinkRelationship> hyperlinks = GetHyperlinks(tempFileStream);

		// Verify all test URLs were replaced
		foreach (KeyValuePair<string, string> kvp in urlsToUpdate)
		{
			hyperlinks.ShouldNotContain(h => h.Uri.ToString().Equals(kvp.Key, StringComparison.InvariantCultureIgnoreCase));
			hyperlinks.ShouldContain(h => h.Uri.ToString().Equals(kvp.Value, StringComparison.InvariantCultureIgnoreCase));
		}

		// Control URLs should remain unchanged
		hyperlinks.ShouldContain(h => h.Uri.ToString().Contains("google.com"));
		hyperlinks.ShouldContain(h => h.Uri.ToString().Contains("xkcd.com"));
		hyperlinks.ShouldContain(h => h.Uri.ToString().Contains("github.com"));
	}

	[RetryFact(3)]
	public void ChangeUrlsInWordDoc_Dictionary_KeysDifferingOnlyByCase_FirstAddedKeyWins()
	{
		// Arrange
		Dictionary<string, string> urlsToUpdate = new()
			{
				{ "http://TestUrl/1", "http://NewUrl/First" },
				{ "HTTP://TESTURL/1", "http://NewUrl/Second" }
			};

		// Act
		bool result = CommonNetFuncs.Word.OpenXml.ChangeUrls.ChangeUrlsInWordDoc(tempFileStream!, urlsToUpdate);
		tempFileStream!.Position = 0;

		// Assert
		result.ShouldBeTrue();
		List<HyperlinkRelationship> hyperlinks = GetHyperlinks(tempFileStream);
		hyperlinks.ShouldNotContain(h => h.Uri.ToString().Equals("http://TestUrl/1", StringComparison.InvariantCultureIgnoreCase));
		hyperlinks.ShouldContain(h => h.Uri.ToString().Equals("http://NewUrl/First", StringComparison.InvariantCultureIgnoreCase));
		hyperlinks.ShouldNotContain(h => h.Uri.ToString().Equals("http://NewUrl/Second", StringComparison.InvariantCultureIgnoreCase));
	}

	[RetryTheory(3)]
	[InlineData(@"(TestUrl/)(\d+)", "NewUrl/$2", true)]
	[InlineData("TestUrl/1", "NewUrl/First", false)]
	public void ChangeUrlsInWordDocRegex_SinglePattern_ReplacesCorrectly(string pattern, string replacement, bool replaceAll)
	{
		// Act
		bool result = CommonNetFuncs.Word.OpenXml.ChangeUrls.ChangeUrlsInWordDocRegex(tempFileStream!, pattern, replacement, replaceAll, RegexOptions.IgnoreCase);
		tempFileStream!.Position = 0;

		// Assert
		result.ShouldBeTrue();
		IEnumerable<HyperlinkRelationship> hyperlinks = GetHyperlinks(tempFileStream);

		// Verify the pattern matches were replaced
		Regex regex = new(pattern, RegexOptions.IgnoreCase);
		int remainingMatches = hyperlinks.Count(h => regex.IsMatch(h.Uri.ToString()));
		remainingMatches.ShouldBe(replaceAll ? 0 : 1); // Should be 0 if replaceAll, otherwise 2 (as one was replaced)

		// Control URLs should remain unchanged
		hyperlinks.ShouldContain(h => h.Uri.ToString().Contains("google.com"));
		hyperlinks.ShouldContain(h => h.Uri.ToString().Contains("xkcd.com"));
		hyperlinks.ShouldContain(h => h.Uri.ToString().Contains("github.com"));
	}

	[RetryTheory(3)]
	[InlineData("TestUrl", true, false)]
	[InlineData("TestUrl", false, true)]
	[InlineData("testurl", true, true)]
	[InlineData("testurl", false, true)]
	public void ChangeUrlsInWordDocRegex_Dictionary_ReplacesMultiplePatterns(string regexPattern, bool caseSensitive, bool shouldSucceed)
	{
		// Arrange
		Dictionary<string, string> patternsToUpdate = new()
				{
						{ $"{regexPattern}/1", "NewUrl/First" },
						{ $"{regexPattern}/2", "NewUrl/Second" },
						{ $"{regexPattern}/3", "NewUrl/Third" }
				};

		// Act
		bool result = CommonNetFuncs.Word.OpenXml.ChangeUrls.ChangeUrlsInWordDocRegex(tempFileStream!, patternsToUpdate, regexOptions: caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
		tempFileStream!.Position = 0;

		// Assert
		result.ShouldBeTrue();
		IEnumerable<HyperlinkRelationship> hyperlinks = GetHyperlinks(tempFileStream);

		// Verify all patterns were replaced
		if (shouldSucceed)
		{
			foreach (KeyValuePair<string, string> kvp in patternsToUpdate)
			{
				hyperlinks.ShouldNotContain(h => h.Uri.ToString().Contains(kvp.Key, StringComparison.InvariantCultureIgnoreCase));
				hyperlinks.ShouldContain(h => h.Uri.ToString().Contains(kvp.Value, StringComparison.InvariantCultureIgnoreCase));
			}
		}
		else
		{
			foreach (KeyValuePair<string, string> kvp in patternsToUpdate)
			{
				hyperlinks.ShouldContain(h => h.Uri.ToString().Contains(kvp.Key, StringComparison.InvariantCultureIgnoreCase));
				hyperlinks.ShouldNotContain(h => h.Uri.ToString().Contains(kvp.Value, StringComparison.InvariantCultureIgnoreCase));
			}
		}

		// Control URLs should remain unchanged
		hyperlinks.ShouldContain(h => h.Uri.ToString().Contains("google.com"));
		hyperlinks.ShouldContain(h => h.Uri.ToString().Contains("xkcd.com"));
		hyperlinks.ShouldContain(h => h.Uri.ToString().Contains("github.com"));
	}

	[RetryFact(3)]
	public void ChangeUrlsInWordDoc_InvalidStream_ReturnsFalse()
	{
		// Arrange
		using MemoryStream invalidStream = new(new byte[] { 0x0 });

		// Act
		bool result = CommonNetFuncs.Word.OpenXml.ChangeUrls.ChangeUrlsInWordDoc(invalidStream, "http://NewUrl/1", "http://TestUrl/1");

		// Assert
		result.ShouldBeFalse();
	}

	[RetryFact(3)]
	public void ChangeUrlsInWordDocRegex_InvalidPattern_ReturnsFalse()
	{
		// Arrange
		const string invalidPattern = "["; // Invalid regex pattern

		// Act
		bool result = CommonNetFuncs.Word.OpenXml.ChangeUrls.ChangeUrlsInWordDocRegex(tempFileStream!, invalidPattern, "replacement");

		// Assert
		result.ShouldBeFalse();
	}
}

public sealed class WordHyperlinkBehaviorTests
{
	private static readonly string TestDocument = Path.Combine(AppContext.BaseDirectory, "TestData", "TestDocument.docx");







	private static MemoryStream CopyTestDocument()
	{
		MemoryStream stream = new(File.ReadAllBytes(TestDocument));
		stream.Position = 0;
		return stream;
	}

	private static MemoryStream CreatePackageWithoutMainPart()
	{
		MemoryStream stream = new();
		using (WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
		{
		}
		stream.Position = 0;
		return stream;
	}

	private static string[] ReadHyperlinks(Stream stream)
	{
		using WordprocessingDocument document = WordprocessingDocument.Open(stream, false);
		return document.MainDocumentPart!.HyperlinkRelationships.Select(x => x.Uri.ToString()).ToArray();
	}

	[Fact]
	public void ChangeUrlsInWordDoc_DictionaryWithNoLinksOrMainPart_ReturnsFalse()
	{
		using MemoryStream stream = CreatePackageWithoutMainPart();

		bool result = ChangeUrls.ChangeUrlsInWordDoc(stream, new Dictionary<string, string>
		{
			["http://TestUrl/1"] = "http://NewUrl/1"
		});

		result.ShouldBeFalse();
	}

	[Fact]
	public void ChangeUrlsInWordDoc_NullDictionary_IsHandledForDocumentWithHyperlinks()
	{
		using MemoryStream stream = CopyTestDocument();

		bool result = ChangeUrls.ChangeUrlsInWordDoc(stream, null!);

		result.ShouldBeFalse();
	}

	[Fact]
	public void ChangeUrlsInWordDoc_InvalidReplacementUri_ReturnsFalse()
	{
		using MemoryStream stream = CopyTestDocument();

		bool result = ChangeUrls.ChangeUrlsInWordDoc(stream, new Dictionary<string, string>
		{
			["http://TestUrl/1"] = "not a valid uri\0"
		});

		result.ShouldBeFalse();
	}

	[Fact]
	public void ChangeUrlsInWordDocRegex_DictionaryReplaceFirst_LeavesLaterMatches()
	{
		using MemoryStream stream = CopyTestDocument();
		Dictionary<string, string> replacements = new()
		{
			["TestUrl/1"] = "FirstReplacement"
		};

		bool result = ChangeUrls.ChangeUrlsInWordDocRegex(stream, replacements, replaceAll: false);
		stream.Position = 0;
		string[] uris = ReadHyperlinks(stream);

		result.ShouldBeTrue();
		uris.ShouldContain(x => x.Contains("FirstReplacement", StringComparison.OrdinalIgnoreCase));
		uris.Count(x => x.Contains("TestUrl/1", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
		uris.ShouldContain(x => x.Contains("TestUrl/2", StringComparison.OrdinalIgnoreCase));
		uris.ShouldContain(x => x.Contains("TestUrl/3", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void ChangeUrlsInWordDocRegex_DictionaryInvalidPattern_ReturnsFalse()
	{
		using MemoryStream stream = CopyTestDocument();

		bool result = ChangeUrls.ChangeUrlsInWordDocRegex(stream, new Dictionary<string, string>
		{
			["["] = "replacement"
		});

		result.ShouldBeFalse();
	}

	[Fact]
	public void ChangeUrlsInWordDocRegex_DocumentWithoutMainPart_ReturnsFalse()
	{
		using MemoryStream stream = CreatePackageWithoutMainPart();

		bool result = ChangeUrls.ChangeUrlsInWordDocRegex(
			stream,
			"TestUrl",
			"NewUrl",
			regexOptions: RegexOptions.None,
			regexTimeout: TimeSpan.FromSeconds(1));

		result.ShouldBeFalse();
	}

}
