using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using static System.Web.HttpUtility;

namespace CommonNetFuncs.Email;

public static partial class HtmlEmailBuilder
{
	const string LineBreak = "<br><br>";
	const string CloseTr = "</tr>";
	private const int MaxSinglePassUrlMatches = 32;

#if !NETSTANDARD2_1
	[GeneratedRegex(@"https?://[^\n\t< ]+", RegexOptions.IgnoreCase | RegexOptions.Compiled, 5000, "en-US")]
	private static partial Regex UrlRegex();
#else
	private static readonly Regex urlRegexInstance = new(@"https?://[^\n\t< ]+", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(5));
	private static Regex UrlRegex() => urlRegexInstance;
#endif

	/// <summary>
	/// Creates an HTML body for an email using the inputs provided
	/// </summary>
	/// <param name="body">Main text body of the email that goes before a table if there is one</param>
	/// <param name="footer">Any text to be displayed under the table or after the body</param>
	/// <returns>HTML Body of an email</returns>
	public static string BuildHtmlEmail(string body, string? footer = null)
	{
		string htmlBody = body.StringToHtml();
		StringBuilder emailTextBuilder = new("<html><body>");
		emailTextBuilder.Append(htmlBody);
		emailTextBuilder.Append(htmlBody);
		emailTextBuilder.Append(!string.IsNullOrWhiteSpace(footer) ? LineBreak : string.Empty);
		emailTextBuilder.Append(footer.StringToHtml());
		emailTextBuilder.Append("</body></html>");
		return emailTextBuilder.ToString().FormatAllUrlsToHtml();
	}

	/// <summary>
	/// Creates an HTML body for an email using the inputs provided
	/// </summary>
	/// <param name="body">Main text body of the email that goes before a table if there is one</param>
	/// <param name="tableData">Data to be formatted into an HTML table.</param>
	/// <param name="footer">Any text to be displayed under the table or after the body</param>
	/// <returns>HTML Body of an email</returns>
	public static string BuildHtmlEmail(string body, DataTable? tableData, string? footer = null, CancellationToken cancellationToken = default)
	{
		string htmlBody = body.StringToHtml();
		StringBuilder emailTextBuilder = new("<html><body>");
		emailTextBuilder.Append(htmlBody);
		emailTextBuilder.Append(htmlBody);
		emailTextBuilder.Append(tableData == null || tableData.Rows.Count == 0 ? string.Empty : LineBreak);
		emailTextBuilder.Append(tableData.CreateHtmlTable(cancellationToken: cancellationToken));
		emailTextBuilder.Append(!string.IsNullOrWhiteSpace(footer) ? LineBreak : string.Empty);
		emailTextBuilder.Append(footer.StringToHtml());
		emailTextBuilder.Append("</body></html>");
		return emailTextBuilder.ToString().FormatAllUrlsToHtml();
	}

	/// <summary>
	/// Creates an HTML body for an email using the inputs provided
	/// </summary>
	/// <param name="body">Main text body of the email that goes before a table if there is one</param>
	/// <param name="tableData">Data to be formatted into a table. First item should be the header data</param>
	/// <param name="footer">Any text to be displayed under the table or after the body</param>
	/// <returns>HTML Body of an email</returns>
	public static string BuildHtmlEmail(string body, List<List<string>>? tableData, string? footer = null, CancellationToken cancellationToken = default)
	{
		tableData ??= [];

		StringBuilder emailTextBuilder = new("<html><body>");
		emailTextBuilder.Append(body.StringToHtml());
		emailTextBuilder.Append(tableData.Count == 0 ? string.Empty : LineBreak);
		emailTextBuilder.Append(tableData.CreateHtmlTable(cancellationToken: cancellationToken));
		emailTextBuilder.Append(!string.IsNullOrWhiteSpace(footer) ? LineBreak : string.Empty);
		emailTextBuilder.Append(footer.StringToHtml());
		emailTextBuilder.Append("</body></html>");

		return emailTextBuilder.ToString().FormatAllUrlsToHtml();
	}

	/// <summary>
	/// Replace line breaks with <br> and tabs with &nbsp&nbsp&nbsp to be HTML compatible
	/// </summary>
	/// <param name="text">String to be formatted</param>
	/// <returns>HTML compatible text</returns>
	public static string StringToHtml(this string? text)
	{
		if (text == null)
		{
			return string.Empty;
		}

		// Single pass equivalent of Replace(Environment.NewLine, "\r").Replace("\n", "\r").Replace("\r", "<br>").Replace("\t", "&nbsp&nbsp&nbsp"):
		// each Environment.NewLine (greedy, left to right) -> one <br>, every other '\r' or '\n' -> <br>, '\t' -> 3x &nbsp
		// (where NewLine is "\n", "\r\n" still becomes "<br><br>")
		string encoded = HtmlEncode(text);
		int next = encoded.AsSpan().IndexOfAny('\r', '\n', '\t');
		if (next < 0)
		{
			return encoded;
		}

		string newLine = Environment.NewLine;
		StringBuilder sb = new(encoded.Length + 64);
		int position = 0;
		while (next >= 0)
		{
			sb.Append(encoded, position, next - position);
			if (encoded[next] == '\t')
			{
				sb.Append("&nbsp&nbsp&nbsp");
				position = next + 1;
			}
			else
			{
				sb.Append("<br>");
				position = next + (encoded.AsSpan(next).StartsWith(newLine.AsSpan()) ? newLine.Length : 1);
			}

			int relative = encoded.AsSpan(position).IndexOfAny('\r', '\n', '\t');
			next = relative < 0 ? -1 : position + relative;
		}
		sb.Append(encoded, position, encoded.Length - position);
		return sb.ToString();
	}

	/// <summary>
	/// Use regex to find all URLs, ASSUMES URL ENDS WITH WHITESPACE
	/// </summary>
	/// <param name="text">Text to search for and format urls in</param>
	/// <param name="linkText">Text to display for link</param>
	/// <returns>HTML Body with formatted HTML links</returns>
	public static string FormatAllUrlsToHtml(this string text, string? linkText = null)
	{
		MatchCollection matches = UrlRegex().Matches(text);
		int count = matches.Count;
		if (count == 0)
		{
			return text;
		}

		// Every ordinal occurrence of a matched URL is either its own match or lies inside another match, and an inserted link can only be
		// re-matched by a later global Replace if that URL is a substring of another URL's link. When that cannot happen, splicing each link in
		// at its match position in one pass is identical to the sequential global Replace calls; otherwise use those Replace calls.
		if (count <= MaxSinglePassUrlMatches)
		{
			string resolvedLinkText = linkText ?? "Click Here";
			string[] urls = new string[count];
			string[] links = new string[count];
			int extraLength = 0;
			for (int i = 0; i < count; i++)
			{
				urls[i] = matches[i].Value;
				links[i] = urls[i].CreateHtmlLink(resolvedLinkText);
				extraLength += links[i].Length - urls[i].Length;
			}

			if (CanSpliceUrls(urls, links))
			{
				StringBuilder sb = new(text.Length + extraLength);
				int position = 0;
				for (int i = 0; i < count; i++)
				{
					Match match = matches[i];
					sb.Append(text, position, match.Index - position).Append(links[i]);
					position = match.Index + match.Length;
				}
				sb.Append(text, position, text.Length - position);
				return sb.ToString();
			}
		}

		StringBuilder textToFormat = new(text);
		foreach (string url in matches.Select(x => x.Value))
		{
			textToFormat.Replace(url, url.CreateHtmlLink(linkText ?? "Click Here"));
		}
		return textToFormat.ToString();
	}

	private static bool CanSpliceUrls(string[] urls, string[] links)
	{
		for (int i = 0; i < urls.Length; i++)
		{
			for (int j = 0; j < links.Length; j++)
			{
				if (i != j && links[j].Contains(urls[i], StringComparison.Ordinal))
				{
					return false;
				}
			}
		}
		return true;
	}

	/// <summary>
	/// Creates a link behind the given text value
	/// </summary>
	/// <param name="url">Url to embed behind he text</param>
	/// <param name="linkText">Text to display for link</param>
	/// <returns>Formatted HTML link</returns>
	public static string CreateHtmlLink(this string url, string linkText = "Click Here")
	{
		return $"<a href=\"{url}\">{linkText}</a>";
	}

	/// <summary>
	/// Create an HTML table from given data
	/// </summary>
	/// <param name="tableData">Data to turn into an HTML table.</param>
	/// <param name="applyTableCss">Apply CSS styling to table.</param>
	/// <param name="customCss">Custom CSS to apply to the table. If not provided, default will be used.</param>
	/// <returns>HTML table based on the data passed in</returns>
	public static string CreateHtmlTable(this DataTable? tableData, bool applyTableCss = true, string? customCss = null, CancellationToken cancellationToken = default)
	{
		StringBuilder tableHtmlBuilder = new();

		if (tableData != null && tableData.Rows.Count != 0)
		{
			string tableStyle = !string.IsNullOrWhiteSpace(customCss) ? customCss :
				"<style>" +
					"table{" +
						"font-family: arial, sans-serif;" +
						"border-collapse: collapse;" +
						"width: 100%;" +
					"}" +
					"td, th {" +
						"border: 1px solid #dddddd;" +
						"text-align: left;" +
						"padding: 8px;" +
					"}" +
					"tr:nth-child(even) {" +
						"background-color: #dddddd;" +
					"}" +
				"</style>";

			tableHtmlBuilder.Append(applyTableCss ? tableStyle : string.Empty);

			//Make headers
			tableHtmlBuilder.Append("<table><tr>");
			foreach (DataColumn column in tableData.Columns)
			{
				tableHtmlBuilder.Append("<th>").Append(column.ColumnName).Append("</th>");
			}
			tableHtmlBuilder.Append(CloseTr);

			//Add data rows
			int columnCount = tableData.Columns.Count;
			foreach (DataRow rowData in tableData.Rows)
			{
				cancellationToken.ThrowIfCancellationRequested();
				tableHtmlBuilder.Append("<tr>");
				for (int i = 0; i < columnCount; i++)
				{
					tableHtmlBuilder.Append("<td>").Append(rowData[i]?.ToString()).Append("</td>");
				}
				tableHtmlBuilder.Append(CloseTr);
			}
			tableHtmlBuilder.Append("</table>");
		}
		return tableHtmlBuilder.ToString();
	}

	/// <summary>
	/// Create an HTML table from given data
	/// </summary>
	/// <param name="tableData">Data to turn into an HTML table. The first item should contain the header values</param>
	/// <param name="applyTableCss">Apply CSS styling to table.</param>
	/// <param name="customCss">Custom CSS to apply to the table. If not provided, default will be used.</param>
	/// <returns>HTML table based on the data passed in</returns>
	public static string CreateHtmlTable(this List<List<string>>? tableData, bool applyTableCss = true, string? customCss = null, CancellationToken cancellationToken = default)
	{
		tableData ??= [];
		StringBuilder tableHtmlBuilder = new();
		if (tableData.Count > 0)
		{
			string tableStyle = !string.IsNullOrWhiteSpace(customCss) ? customCss :
				"<style>" +
						"table{" +
							"font-family: arial, sans-serif;" +
							"border-collapse: collapse;" +
							"width: 100%;" +
						"}" +
						"td, th {" +
							"border: 1px solid #dddddd;" +
							"text-align: left;" +
							"padding: 8px;" +
						"}" +
						"tr:nth-child(even) {" +
							"background-color: #dddddd;" +
						"}" +
				"</style>";

			tableHtmlBuilder.Append(applyTableCss ? tableStyle : string.Empty);

			List<string> tableHeaders = tableData[0];

			//Make headers
			tableHtmlBuilder.Append("<table><tr>");
			foreach (string header in tableHeaders)
			{
				tableHtmlBuilder.Append("<th>").Append(header).Append("</th>");
			}
			tableHtmlBuilder.Append(CloseTr);

			//Add data rows
			for (int rowIndex = 1; rowIndex < tableData.Count; rowIndex++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				tableHtmlBuilder.Append("<tr>");
				foreach (string cell in tableData[rowIndex])
				{
					tableHtmlBuilder.Append("<td>").Append(cell).Append("</td>");
				}
				tableHtmlBuilder.Append(CloseTr);
			}
			tableHtmlBuilder.Append("</table>");
		}
		return tableHtmlBuilder.ToString();
	}
}
