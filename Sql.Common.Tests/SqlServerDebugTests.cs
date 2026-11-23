#if CORE_NATIVE_BUILD

using System.Data;
using Microsoft.Data.SqlClient;
using SqlServerDebug = CommonNetFuncs.Sql.SqlServer.Debug;

namespace Sql.Common.Tests;

public sealed class SqlServerDebugTests
{
	[Fact]
	public void RenderCommandAsScript_ShouldRenderCommandText_WhenNoParameters()
	{
		using SqlCommand cmd = new("SELECT * FROM TestTable");

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldNotContain("Parameters");
		result.ShouldContain("-- ==== Command ====");
		result.ShouldContain("SELECT * FROM TestTable");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldDeclareVarcharParameter_WithSize()
	{
		using SqlCommand cmd = new("SELECT * FROM TestTable WHERE Name = @Name");
		cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 50) { Value = "O'Brian" });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("DECLARE @Name NVARCHAR(50) = N'O''Brian';");
		result.ShouldContain("SELECT * FROM TestTable WHERE Name = @Name");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldDeclareVarcharParameter_AsMax_WhenSizeIsZeroOrNegative()
	{
		using SqlCommand cmd = new("SELECT @Description");
		cmd.Parameters.Add(new SqlParameter("@Description", SqlDbType.NVarChar, -1) { Value = "text" });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("DECLARE @Description NVARCHAR(MAX) = N'text';");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldAddAtSignPrefix_WhenParameterNameLacksIt()
	{
		using SqlCommand cmd = new("SELECT @Id");
		cmd.Parameters.Add(new SqlParameter("Id", SqlDbType.Int) { Value = 5 });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("DECLARE @Id INT = 5;");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldDeclareDecimalParameter_WithPrecisionAndScale()
	{
		using SqlCommand cmd = new("SELECT @Amount");
		SqlParameter parameter = new("@Amount", SqlDbType.Decimal) { Precision = 10, Scale = 2, Value = 123.45m };
		cmd.Parameters.Add(parameter);

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("DECLARE @Amount DECIMAL(10,2) = 123.45;");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldDeclareDecimalParameter_WithDefaultPrecision_WhenPrecisionIsZero()
	{
		// Use a FakeDbParameter so SqlParameter's auto-inferred Precision/Scale from Value doesn't mask the zero-precision default path
		using FakeDbCommand cmd = new() { CommandText = "SELECT @Amount" };
		cmd.Parameters.Add(new FakeDbParameter { ParameterName = "@Amount", DbType = DbType.Decimal, Value = 1.5m });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("DECLARE @Amount DECIMAL(18,0) = 1.5;");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatNullValue_AsNull()
	{
		using SqlCommand cmd = new("SELECT @Value");
		cmd.Parameters.Add(new SqlParameter("@Value", SqlDbType.NVarChar, 50) { Value = null });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("= NULL;");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatDbNullValue_AsNull()
	{
		using SqlCommand cmd = new("SELECT @Value");
		cmd.Parameters.Add(new SqlParameter("@Value", SqlDbType.Int) { Value = DBNull.Value });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("= NULL;");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatBooleanValue_AsBit()
	{
		using SqlCommand cmd = new("SELECT @Flag");
		cmd.Parameters.Add(new SqlParameter("@Flag", SqlDbType.Bit) { Value = true });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("= 1;");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatDateTimeValue_WithQuotes()
	{
		using SqlCommand cmd = new("SELECT @CreatedDate");
		DateTime date = new(2024, 5, 17, 13, 45, 30, 123, DateTimeKind.Unspecified);
		cmd.Parameters.Add(new SqlParameter("@CreatedDate", SqlDbType.DateTime2) { Value = date });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("= '2024-05-17 13:45:30.123';");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatDateTimeOffsetValue_WithOffset()
	{
		using SqlCommand cmd = new("SELECT @CreatedDate");
		DateTimeOffset date = new(2024, 5, 17, 13, 45, 30, 123, TimeSpan.FromHours(-5));
		cmd.Parameters.Add(new SqlParameter("@CreatedDate", SqlDbType.DateTimeOffset) { Value = date });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("= '2024-05-17 13:45:30.123 -05:00';");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatGuidValue_WithQuotes()
	{
		using SqlCommand cmd = new("SELECT @Id");
		Guid id = Guid.Parse("11111111-2222-3333-4444-555555555555");
		cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.UniqueIdentifier) { Value = id });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("= '11111111-2222-3333-4444-555555555555';");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatByteArrayValue_AsHexLiteral()
	{
		using SqlCommand cmd = new("SELECT @Data");
		byte[] data = [0xDE, 0xAD, 0xBE, 0xEF];
		cmd.Parameters.Add(new SqlParameter("@Data", SqlDbType.VarBinary) { Value = data });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("= 0xDEADBEEF;");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatNumericValues_UsingInvariantCulture()
	{
		using SqlCommand cmd = new("SELECT @IntVal, @DoubleVal, @LongVal");
		cmd.Parameters.Add(new SqlParameter("@IntVal", SqlDbType.Int) { Value = 42 });
		cmd.Parameters.Add(new SqlParameter("@DoubleVal", SqlDbType.Float) { Value = 3.14 });
		cmd.Parameters.Add(new SqlParameter("@LongVal", SqlDbType.BigInt) { Value = 123456789012345L });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("= 42;");
		result.ShouldContain("= 3.14;");
		result.ShouldContain("= 123456789012345;");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldInferSqlDbType_WhenParameterIsNotSqlParameter()
	{
		using FakeDbCommand cmd = new() { CommandText = "SELECT @Value" };
		cmd.Parameters.Add(new FakeDbParameter { ParameterName = "@Value", Value = 42 });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("DECLARE @Value INT = 42;");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldEscapeUnmatchedType_AsQuotedNString()
	{
		using FakeDbCommand cmd = new() { CommandText = "SELECT @Value" };
		cmd.Parameters.Add(new FakeDbParameter { ParameterName = "@Value", Value = new Uri("https://example.com") });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("= N'https://example.com/';");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldRenderMultipleParameters_InOrder()
	{
		using SqlCommand cmd = new("SELECT * FROM TestTable WHERE Id = @Id AND Name = @Name");
		cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = 1 });
		cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 50) { Value = "Test" });

		string result = SqlServerDebug.RenderCommandAsScript(cmd);

		int idIndex = result.IndexOf("DECLARE @Id", StringComparison.Ordinal);
		int nameIndex = result.IndexOf("DECLARE @Name", StringComparison.Ordinal);

		idIndex.ShouldBeGreaterThanOrEqualTo(0);
		nameIndex.ShouldBeGreaterThan(idIndex);
		result.ShouldContain("SELECT * FROM TestTable WHERE Id = @Id AND Name = @Name");
	}
}

public sealed class SqlServerDebugBehaviorTests
{



	public static TheoryData<object, string, string> PostgreSqlInferredValues()
	{
		Guid id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
		return new()
		{
			{ 1L, "bigint", "1" },
			{ (short)2, "smallint", "2" },
			{ (byte)3, "smallint", "3" },
			{ true, "boolean", "TRUE" },
			{ 4.5m, "numeric(18,0)", "4.5" },
			{ 5.5d, "double", "5.5" },
			{ 6.5f, "double", "6.5" },
			{ new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Unspecified), "timestamp", "'2025-02-03 04:05:06.000000'::timestamp" },
			{ new DateTimeOffset(2025, 2, 3, 4, 5, 6, 7, TimeSpan.FromHours(2)), "timestamptz", "'2025-02-03 04:05:06.007000+02:00'::timestamptz" },
			{ id, "uuid", "'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'::uuid" },
			{ new byte[] { 0xAB, 0xCD }, "bytea", "'\\xabcd'::bytea" },
			{ new DateOnly(2025, 2, 3), "date", "'2025-02-03'::date" },
			{ new TimeOnly(4, 5, 6, 7), "time", "'04:05:06.007000'::time" },
			{ new TimeSpan(1, 2, 3, 4), "interval", "'1.02:03:04'::interval" },
			{ new[] { 1, 2 }, "text[]", "ARRAY[1, 2]" }
		};
	}




	[Theory]
	[InlineData(1L, "BIGINT", "1")]
	[InlineData((short)2, "SMALLINT", "2")]
	[InlineData((byte)3, "TINYINT", "3")]
	[InlineData(true, "BIT", "1")]
	[InlineData(4.5d, "FLOAT", "4.5")]
	[InlineData(5.5f, "FLOAT", "5.5")]
	public void SqlServerDebug_WithInferredScalarType_ShouldRenderDeclaration(object value, string typeName, string literal)
	{
		using FakeDbCommand command = new() { CommandText = "SELECT @Value" };
		command.Parameters.Add(new FakeDbParameter { ParameterName = "Value", Value = value });

		string script = SqlServerDebug.RenderCommandAsScript(command);

		script.ShouldContain($"DECLARE @Value {typeName} = {literal};");
	}

	[Fact]
	public void SqlServerDebug_WithInferredComplexScalars_ShouldRenderExactDeclarations()
	{
		Guid id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
		DateTime created = new(2025, 2, 3, 4, 5, 6, DateTimeKind.Unspecified);
		DateTimeOffset updated = new(2025, 2, 3, 4, 5, 6, 7, TimeSpan.FromHours(2));
		using FakeDbCommand command = new() { CommandText = "SELECT @Amount, @Created, @Updated, @Id, @Data" };
		command.Parameters.Add(new FakeDbParameter { ParameterName = "Amount", Value = 6.25m });
		command.Parameters.Add(new FakeDbParameter { ParameterName = "Created", Value = created });
		command.Parameters.Add(new FakeDbParameter { ParameterName = "Updated", Value = updated });
		command.Parameters.Add(new FakeDbParameter { ParameterName = "Id", Value = id });
		command.Parameters.Add(new FakeDbParameter { ParameterName = "Data", Value = new byte[] { 0xAB, 0xCD } });

		string script = SqlServerDebug.RenderCommandAsScript(command);

		script.ShouldContain("DECLARE @Amount DECIMAL(18,0) = 6.25;");
		script.ShouldContain("DECLARE @Created DATETIME2 = '2025-02-03 04:05:06.000';");
		script.ShouldContain("DECLARE @Updated DATETIMEOFFSET = '2025-02-03 04:05:06.007 +02:00';");
		script.ShouldContain("DECLARE @Id UNIQUEIDENTIFIER = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';");
		script.ShouldContain("DECLARE @Data VARBINARY = 0xABCD;");
	}

}
#endif
