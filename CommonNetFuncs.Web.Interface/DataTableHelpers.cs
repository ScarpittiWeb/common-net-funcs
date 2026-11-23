using MemoryPack;
using MessagePack;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Text.Json.Serialization;
using static CommonNetFuncs.Sql.Common.QueryParameters;
using static CommonNetFuncs.Web.Common.ContentTypes;
using static System.Convert;

namespace CommonNetFuncs.Web.Interface;

/// <summary>
/// For use with the DataTables.net JavaScript framework
/// </summary>
public static class DataTableHelpers
{
	private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

	/// <summary>
	/// Parse the DataTables HttpRequest object into the DataTableRequest class
	/// </summary>
	/// <param name="request">HTTP request sent by DataTables</param>
	/// <returns><see cref="DataTable"/>Request object containing the parsed request values</returns>
	public static DataTableRequest GetDataTableRequest(this HttpRequest request)
	{
		DataTableRequest dataTableRequest = new();
		try
		{
			if (FormDataTypes.Any(x => request.ContentType?.Contains(x, StringComparison.InvariantCultureIgnoreCase) ?? false) && request.Form != null)
			{
				// Snapshot the form once (preserving enumeration order) and resolve each key with a single first-match pass
				KeyValuePair<string, StringValues>[] fields = SnapshotForm(request.Form);

				dataTableRequest.Draw = FindFirst(fields, "draw");

				for (int i = 0; TryFindFirst(fields, $"order[{i}][column]", out StringValues orderColumn); i++)
				{
					dataTableRequest.SortColumns.Add(i, FindFirst(fields, "columns[" + orderColumn + "][data]"));
					dataTableRequest.SortColumnDir.Add(i, FindFirst(fields, $"order[{i}][dir]"));
				}

				string? start = FindFirst(fields, "start");
				string? length = FindFirst(fields, "length");
				string? searchValue = FindFirst(fields, "search[value]");

				//Paging Size (10,20,50,100)
				dataTableRequest.PageSize = length != StringValues.Empty ? ToInt32(length) : 0;
				dataTableRequest.Skip = start != StringValues.Empty ? ToInt32(start) : 0;

				//Get search value key pairs
				if (!string.IsNullOrEmpty(searchValue))
				{
					foreach (string val in searchValue.Split(','))
					{
						string cleanVal = val.CleanQueryParam()!;
						int startPos = cleanVal.IndexOf('=') + 1;
						if (startPos == 0)
						{
							continue;
						}
						string key = cleanVal[..(startPos - 1)];
						if (startPos + 1 <= cleanVal.Length)
						{
							int valLength = cleanVal.Length - startPos;
							string outVal = cleanVal.Substring(startPos, valLength);
							dataTableRequest.SearchValues.Add(key, outVal);
						}
						else
						{
							dataTableRequest.SearchValues.Add(key, null);
						}
					}
				}
			}
			else
			{
				logger.Warn($"Unable to read Datatable request: Body is not a valid form data type [{string.Join(", ", FormDataTypes)}], or form data is null");
			}
		}
		catch (Exception ex)
		{
			logger.Error(ex, "{Class}.{Method} Error", $"{nameof(DataTableHelpers)}.{nameof(GetDataTableRequest)}");
		}
		return dataTableRequest;
	}

	private static KeyValuePair<string, StringValues>[] SnapshotForm(IFormCollection form)
	{
		KeyValuePair<string, StringValues>[] fields = new KeyValuePair<string, StringValues>[form.Count];
		int count = 0;
		foreach (KeyValuePair<string, StringValues> field in form)
		{
			if (count == fields.Length)
			{
				Array.Resize(ref fields, (count * 2) + 1);
			}
			fields[count++] = field;
		}

		if (count != fields.Length)
		{
			Array.Resize(ref fields, count);
		}
		return fields;
	}

	private static StringValues FindFirst(KeyValuePair<string, StringValues>[] fields, string key)
	{
		TryFindFirst(fields, key, out StringValues value);
		return value;
	}

	private static bool TryFindFirst(KeyValuePair<string, StringValues>[] fields, string key, out StringValues value)
	{
		foreach (KeyValuePair<string, StringValues> field in fields)
		{
			if (string.Equals(field.Key, key, StringComparison.InvariantCultureIgnoreCase))
			{
				value = field.Value;
				return true;
			}
		}
		value = default;
		return false;
	}

	/// <summary>
	/// Transform DataTableRequest into an object that can be used to limit the results sent back from a query why using paging or applying a sort
	/// </summary>
	/// <param name="request">DataTableRequest to get SortAndLimitPostModel object for</param>
	/// <returns>SortAndLimitPostModel object created from the parameters in DataTableRequest</returns>
	public static SortAndLimitPostModel GetSortAndLimitPostModel(DataTableRequest request)
	{
		return new()
		{
			SortColumns = request.SortColumns,
			SortColumnDir = request.SortColumnDir,
			Skip = request.Skip,
			PageSize = request.PageSize
		};
	}
}

#region Classes

public sealed class DataTableRequest
{
	public DataTableRequest()
	{
		SearchValues = [];
		SortColumnDir = [];
		SortColumns = [];
	}

	public int PageSize { get; set; }

	public int Skip { get; set; }

	public Dictionary<int, string?> SortColumnDir { get; set; }

	public Dictionary<int, string?> SortColumns { get; set; }

	public string? Draw { get; set; }

	public Dictionary<string, string?> SearchValues { get; set; }
}

public sealed class DataTableReturnData<T>
{
	[JsonPropertyName("draw")]
	public string? Draw { get; set; }

	[JsonPropertyName("recordsFiltered")]
	public int? RecordsFiltered { get; set; }

	[JsonPropertyName("recordsTotal")]
	public int? RecordsTotal { get; set; }

	[JsonPropertyName("data")]
	public IEnumerable<T> Data { get; set; } = [];
}

[MemoryPackable]
[MessagePackObject(true)]
public partial class SortAndLimitPostModel
{
	public SortAndLimitPostModel()
	{
		SortColumns = [];
		SortColumnDir = [];
	}

	public Dictionary<int, string?> SortColumns { get; set; }

	public Dictionary<int, string?> SortColumnDir { get; set; }

	public int Skip { get; set; }

	public int PageSize { get; set; }
}

#endregion Classes
