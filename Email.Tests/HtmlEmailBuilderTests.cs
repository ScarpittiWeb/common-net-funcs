using CommonNetFuncs.Email;
using System.Data;
using static CommonNetFuncs.Email.HtmlEmailBuilder;
using static Xunit.TestContext;

namespace Email.Tests;

public sealed class HtmlEmailBuilderTests
{
	[Theory]
	[InlineData("Hello\nWorld", "Hello<br>World")]
	[InlineData("Hello\r\nWorld", "Hello<br>World", "Hello<br><br>World")]
	[InlineData("Hello\tWorld", "Hello&nbsp&nbsp&nbspWorld")]
	[InlineData(null, "")]
	[InlineData("Hello <script>", "Hello &lt;script&gt;")]
	[InlineData("A\rB\n\nC\r\n\tD", "A<br>B<br><br>C<br>&nbsp&nbsp&nbspD", "A<br>B<br><br>C<br><br>&nbsp&nbsp&nbspD")]
	public void StringToHtml_ShouldFormatCorrectly(string? input, string expected, string? unixExpected = null)
	{
		// Act
		string result = input.StringToHtml();

		// Assert
		result.ShouldBe(Environment.OSVersion.Platform == PlatformID.Win32NT ? expected : unixExpected ?? expected);
	}

	[Theory]
	[InlineData("Visit http://example.com today!", "<a href=\"http://example.com\">Click Here</a>")]
	[InlineData("Check https://example.com/path?q=1 now", "<a href=\"https://example.com/path?q=1\">Click Here</a>")]
	[InlineData("No URL here", "No URL here")]
	public void FormatAllUrlsToHtml_ShouldReplaceUrlsWithLinks(string input, string expectedUrl)
	{
		// Act
		string result = input.FormatAllUrlsToHtml();

		// Assert
		result.ShouldContain(expectedUrl);
	}

	[Theory]
	[InlineData("Custom Text")]
	public void FormatAllUrlsToHtml_WithCustomLinkText_ShouldUseProvidedText(string linkText)
	{
		// Arrange
		const string input = "Visit http://example.com today!";
		string expected = $"<a href=\"http://example.com\">{linkText}</a>";

		// Act
		string result = input.FormatAllUrlsToHtml(linkText);

		// Assert
		result.ShouldContain(expected);
	}

	[Theory]
	// Distinct URLs: each one is replaced in place
	[InlineData("See https://a.com/x and http://b.org/y today", "Go",
		"See <a href=\"https://a.com/x\">Go</a> and <a href=\"http://b.org/y\">Go</a> today")]
	// Duplicate URL: every Replace pass also rewrites the links inserted by the previous pass
	[InlineData("a http://x.com b http://x.com c", null,
		"a <a href=\"<a href=\"http://x.com\">Click Here</a>\">Click Here</a> b <a href=\"<a href=\"http://x.com\">Click Here</a>\">Click Here</a> c")]
	// URL that is a prefix of another URL: the shorter one is replaced inside the longer one first
	[InlineData("http://x.com and http://x.com/page", null,
		"<a href=\"http://x.com\">Click Here</a> and <a href=\"http://x.com\">Click Here</a>/page")]
	public void FormatAllUrlsToHtml_Should_Match_Sequential_Replace_Semantics(string input, string? linkText, string expected)
	{
		// Act
		string result = input.FormatAllUrlsToHtml(linkText);

		// Assert
		result.ShouldBe(expected);
	}

	[Fact]
	public void FormatAllUrlsToHtml_Should_Replace_Many_Urls()
	{
		// Arrange - more URLs than the single-pass limit
		string[] urls = Enumerable.Range(0, 40).Select(i => $"https://example.com/{i}/").ToArray();
		string input = string.Join(" text ", urls);
		string expected = string.Join(" text ", urls.Select(x => x.CreateHtmlLink()));

		// Act
		string result = input.FormatAllUrlsToHtml();

		// Assert
		result.ShouldBe(expected);
	}

	[Theory]
	[InlineData("http://example.com", "Link Text", "<a href=\"http://example.com\">Link Text</a>")]
	public void CreateHtmlLink_ShouldCreateValidHtmlLink(string url, string linkText, string expected)
	{
		// Act
		string result = url.CreateHtmlLink(linkText);

		// Assert
		result.ShouldBe(expected);
	}

	[Fact]
	public void BuildHtmlEmail_WithBasicContent_ShouldCreateValidHtml()
	{
		// Arrange
		const string body = "Hello World";
		const string footer = "Goodbye";

		// Act
		string result = BuildHtmlEmail(body, footer);

		// Assert
		result.ShouldStartWith("<html><body>");
		result.ShouldEndWith("</body></html>");
		result.ShouldContain("Hello World");
		result.ShouldContain("Goodbye");
	}

	[Fact]
	public void CreateHtmlTable_WithDataTable_ShouldCreateValidHtmlTable()
	{
		// Arrange
		using DataTable table = new();
		table.Columns.Add("Name");
		table.Columns.Add("Age");
		table.Rows.Add("John", "30");
		table.Rows.Add("Jane", "25");

		// Act
		string result = table.CreateHtmlTable(cancellationToken: Current.CancellationToken);

		// Assert
		result.ShouldContain("<table>");
		result.ShouldEndWith("</table>");
		result.ShouldContain("<th>Name</th>");
		result.ShouldContain("<th>Age</th>");
		result.ShouldContain("<td>John</td>");
		result.ShouldContain("<td>30</td>");
		result.ShouldContain("<td>Jane</td>");
		result.ShouldContain("<td>25</td>");
	}

	[Fact]
	public void CreateHtmlEmail_WithDataTable_ShouldCreateValidHtmlEmailWithTable()
	{
		// Arrange
		const string body = "Hello";
		const string footer = "Goodbye";
		using DataTable table = new();
		table.Columns.Add("Name");
		table.Columns.Add("Age");
		table.Rows.Add("John", "30");
		table.Rows.Add("Jane", "25");

		// Act
		string result = BuildHtmlEmail(body, table, footer, Current.CancellationToken);

		// Assert
		result.ShouldContain("<table>");
		result.ShouldContain("</table>");
		result.ShouldContain("<th>Name</th>");
		result.ShouldContain("<th>Age</th>");
		result.ShouldContain("<td>John</td>");
		result.ShouldContain("<td>30</td>");
		result.ShouldContain("<td>Jane</td>");
		result.ShouldContain("<td>25</td>");
		result.ShouldContain("<br><br>");
		result.ShouldContain(body);
		result.ShouldContain(footer);
	}

	[Fact]
	public void CreateHtmlTable_WithList_ShouldCreateValidHtmlTable()
	{
		// Arrange
		List<List<string>> tableData =
		[
			new() { "Name", "Age" },          // Header row
			new() { "John", "30" },           // Data row 1
			new() { "Jane", "25" }            // Data row 2
		];

		// Act
		string result = tableData.CreateHtmlTable(cancellationToken: Current.CancellationToken);

		// Assert
		result.ShouldContain("<table>");
		result.ShouldContain("<th>Name</th>");
		result.ShouldContain("<th>Age</th>");
		result.ShouldContain("<td>John</td>");
		result.ShouldContain("<td>30</td>");
		result.ShouldContain("<td>Jane</td>");
		result.ShouldContain("<td>25</td>");
	}

	[Fact]
	public void CreateHtmlTable_WithCustomCss_ShouldApplyCustomStyles()
	{
		// Arrange
		List<List<string>> tableData =
		[
			new() { "Header" },
			new() { "Data" }
		];
		const string customCss = "<style>table { color: red; }</style>";

		// Act
		string result = tableData.CreateHtmlTable(customCss: customCss, cancellationToken: Current.CancellationToken);

		// Assert
		result.ShouldContain(customCss);
	}

	[Fact]
	public void CreateHtmlTable_WithoutCss_ShouldNotIncludeStyles()
	{
		// Arrange
		List<List<string>> tableData = new()
		{
			new() { "Header" },
			new() { "Data" }
		};

		// Act
		string result = tableData.CreateHtmlTable(applyTableCss: false, cancellationToken: Current.CancellationToken);

		// Assert
		result.ShouldNotContain("<style>");
	}

	[Fact]
	public void BuildHtmlEmail_WithTableData_ShouldIncludeTableAndSpacing()
	{
		// Arrange
		const string body = "Hello";
		const string footer = "Goodbye";
		List<List<string>> tableData = new()
		{
			new() { "Header" },
			new() { "Data" }
		};

		// Act
		string result = BuildHtmlEmail(body, tableData, footer, Current.CancellationToken);

		// Assert
		result.ShouldContain("<br><br>");
		result.ShouldContain("<table>");
		result.ShouldContain(body);
		result.ShouldContain(footer);
	}

	[Fact]
	public void CreateHtmlTable_WithEmptyData_ShouldReturnEmptyString()
	{
		// Arrange
		using DataTable? emptyTable = null;
		List<List<string>>? emptyList = null;

		// Act
		string resultFromTable = emptyTable.CreateHtmlTable(cancellationToken: Current.CancellationToken);
		string resultFromList = emptyList.CreateHtmlTable(cancellationToken: Current.CancellationToken);

		// Assert
		resultFromTable.ShouldBe(string.Empty);
		resultFromList.ShouldBe(string.Empty);
	}

	[Fact]
	public async Task CreateHtmlTable_WithCancellation_ShouldRespectCancellationToken()
	{
		// Arrange
		using CancellationTokenSource cts = new();
		List<List<string>> tableData = new()
		{
			new() { "Header" },
			new() { "Data1" },
			new() { "Data2" }
		};
		await cts.CancelAsync();

		// Act & Assert
		await Should.ThrowAsync<OperationCanceledException>(() =>
		{
			_ = tableData.CreateHtmlTable(cancellationToken: cts.Token);
			return Task.CompletedTask;
		});
	}

	[Theory]
	[InlineData(true, "<br><br>")]
	[InlineData(false, "")]
	public void BuildHtmlEmail_WithOptionalFooter_ShouldHandleSpacingCorrectly(bool includeFooter, string expectedSpacing)
	{
		// Arrange
		const string body = "Hello";
		string? footer = includeFooter ? "Footer" : null;

		// Act
		string result = BuildHtmlEmail(body, footer);

		// Assert
		if (includeFooter)
		{
			result.ShouldContain(expectedSpacing);
		}
		else
		{
			result.ShouldNotContain("<br><br>");
		}
	}
}

public sealed class HtmlEmailBuilderBehaviorTests
{
	private sealed class ThrowingAttachment : IMailAttachment
	{
		public string? AttachmentName => throw new InvalidOperationException("Cannot serialize attachment name.");

		string? IMailAttachment.AttachmentName
		{
			get => AttachmentName;
			set { }
		}

		public Stream? GetStream() => null;
	}

	private sealed class StaticAttachment(string? name, Stream? stream) : IMailAttachment
	{
		public string? AttachmentName { get; set; } = name;

		public Stream? GetStream() => stream;
	}












	[Fact]
	public void CreateHtmlTable_DataTableOptionsAndNullCellsAreEmittedVerbatim()
	{
		using DataTable table = new();
		table.Columns.Add("Name");
		table.Columns.Add("Optional", typeof(string));
		table.Rows.Add("Ada", DBNull.Value);

		string result = table.CreateHtmlTable(applyTableCss: false, customCss: "ignored", cancellationToken: Current.CancellationToken);

		result.ShouldBe("<table><tr><th>Name</th><th>Optional</th></tr><tr><td>Ada</td><td></td></tr></table>");
	}

	[Fact]
	public void BuildHtmlEmail_NullAndEmptyTablesDoNotAddTableBreaks()
	{
		BuildHtmlEmail("Body", (DataTable?)null, cancellationToken: Current.CancellationToken).ShouldBe("<html><body>BodyBody</body></html>");
		BuildHtmlEmail("Body", (List<List<string>>?)null, cancellationToken: Current.CancellationToken).ShouldBe("<html><body>Body</body></html>");

		using DataTable empty = new();
		BuildHtmlEmail("Body", empty, cancellationToken: Current.CancellationToken).ShouldBe("<html><body>BodyBody</body></html>");
		BuildHtmlEmail("Body", new List<List<string>>(), cancellationToken: Current.CancellationToken).ShouldBe("<html><body>Body</body></html>");
	}

	[Fact]
	public void BuildHtmlEmail_WhitespaceFooterDoesNotAddBreak()
	{
		BuildHtmlEmail("Body", "   ").ShouldBe("<html><body>BodyBody   </body></html>");
		BuildHtmlEmail("Body", (List<List<string>>?)null, "\t", Current.CancellationToken).ShouldBe("<html><body>Body&nbsp&nbsp&nbsp</body></html>");
	}

}
