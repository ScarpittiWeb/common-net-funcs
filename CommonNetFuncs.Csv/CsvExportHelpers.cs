using CsvHelper;
using System.Data;
using System.Globalization;
using System.Text;
using static CommonNetFuncs.Core.ExceptionLocation;
using static CommonNetFuncs.Core.Streams;
using static System.Convert;

namespace CommonNetFuncs.Csv;

public static class CsvExportHelpers
{
	private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

	/// <summary>
	/// Writes collection of values to a CSV file in a <see cref="MemoryStream"/>.
	/// </summary>
	/// <typeparam name="T">Object type to write to CSV format.</typeparam>
	/// <param name="dataList">Data to be inserted into the CSV file.</param>
	/// <param name="memoryStream">Optional: <see cref="MemoryStream"/> to contain the CSV file data. If null, a new <see cref="MemoryStream"/> will be created.</param>
	/// <param name="cancellationToken">Optional: The cancellation token for this request.</param>
	/// <returns><see cref="MemoryStream"/> containing the CSV file data</returns>
	public static async Task<MemoryStream> ExportToCsv<T>(this IEnumerable<T> dataList, MemoryStream? memoryStream = null, CancellationToken cancellationToken = default)
	{
		memoryStream ??= new();
		await using StreamWriter streamWriter = new(memoryStream, new UTF8Encoding(false), 1024, true);
		await using CsvWriter csvWriter = CsvWriter.Create(streamWriter, new()
		{
			FormatProvider = CultureInfo.InvariantCulture
		});

		try
		{
			await csvWriter.WriteRecordsAsync(dataList, cancellationToken).ConfigureAwait(false);
			await csvWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			logger.Error(ex, ErrorLocationTemplate, ex.GetLocationOfException());
		}

		memoryStream.Position = 0;
		return memoryStream;
	}

	/// <summary>
	/// Writes contents of a <see cref="DataTable"/> object to a CSV file in a <see cref="MemoryStream"/>.
	/// </summary>
	/// <param name="dataTable">Data to be inserted into the CSV file.</param>
	/// <param name="memoryStream">Optional: <see cref="MemoryStream"/> to contain the CSV file data. If null, a new <see cref="MemoryStream"/> will be created.</param>
	/// <param name="cancellationToken">Optional: The cancellation token for this request.</param>
	/// <returns><see cref="MemoryStream"/> containing the CSV file data.</returns>
	public static async Task<MemoryStream> ExportToCsv(this DataTable dataTable, MemoryStream? memoryStream = null, CancellationToken cancellationToken = default)
	{
		memoryStream ??= new();
		await using MemoryStream sourceMemoryStream = new();
		await using StreamWriter streamWriter = new(sourceMemoryStream, new UTF8Encoding(false));
		try
		{
			// The writer targets an in-memory stream, so synchronous writes avoid a Task/state machine per cell without blocking on I/O
			int columnCount = dataTable.Columns.Count;

			//Headers
			for (int i = 0; i < columnCount; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				streamWriter.Write(dataTable.Columns[i]?.ToString() ?? string.Empty);
				if (i < columnCount - 1)
				{
					streamWriter.Write(',');
				}
			}
			streamWriter.Write(streamWriter.NewLine);
			foreach (DataRow row in dataTable.Rows)
			{
				cancellationToken.ThrowIfCancellationRequested();
				for (int i = 0; i < columnCount; i++)
				{
					object cell = row[i];
					if (!IsDBNull(cell))
					{
						string? value = cell.ToString();
						if (value?.Contains(',') ?? false)
						{
							streamWriter.Write('"');
							streamWriter.Write(value);
							streamWriter.Write('"');
						}
						else
						{
							streamWriter.Write(value ?? string.Empty);
						}
					}
					if (i < columnCount - 1)
					{
						streamWriter.Write(',');
					}
				}
				streamWriter.Write(streamWriter.NewLine);
			}

			cancellationToken.ThrowIfCancellationRequested();
#if !NETSTANDARD2_1
			await streamWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
#else
			await streamWriter.FlushAsync().ConfigureAwait(false);
#endif
			sourceMemoryStream.Position = 0;
			await memoryStream.WriteStreamToStream(sourceMemoryStream, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			logger.Error(ex, ErrorLocationTemplate, ex.GetLocationOfException());
		}
		finally
		{
			streamWriter.Close();
			await streamWriter.DisposeAsync().ConfigureAwait(false);

			sourceMemoryStream.Close();
			await sourceMemoryStream.DisposeAsync().ConfigureAwait(false);
		}

		return memoryStream;
	}
}
