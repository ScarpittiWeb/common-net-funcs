#if CORE_NATIVE_BUILD

using Npgsql;
using NpgsqlTypes;
using PostgreSqlDebug = CommonNetFuncs.Sql.PostgreSql.Debug;

namespace Sql.Common.Tests;

public sealed class PostgreSqlDebugTests
{
	// NpgsqlDbType is not a [Flags] enum, but Npgsql composes array types with a bitwise OR
	private const NpgsqlDbType TextArray = (NpgsqlDbType)((int)NpgsqlDbType.Array | (int)NpgsqlDbType.Text);
	private const NpgsqlDbType IntegerArray = (NpgsqlDbType)((int)NpgsqlDbType.Array | (int)NpgsqlDbType.Integer);

	[Fact]
	public void RenderCommandAsScript_ShouldRenderCommandText_WhenNoParameters()
	{
		using NpgsqlCommand cmd = new("SELECT * FROM test_table");

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldNotContain("Parameters");
		result.ShouldContain("-- ==== Command ====");
		result.ShouldContain("SELECT * FROM test_table");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldInlineStringParameter_WithAtSignPlaceholder()
	{
		using NpgsqlCommand cmd = new("SELECT * FROM test_table WHERE name = @name");
		cmd.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Varchar) { Value = "O'Brian" });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("-- @name [varchar(unbounded)] = 'O''Brian'");
		result.ShouldContain("SELECT * FROM test_table WHERE name = 'O''Brian'");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldInlineStringParameter_WithColonPlaceholder()
	{
		using NpgsqlCommand cmd = new("SELECT * FROM test_table WHERE name = :name");
		cmd.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Varchar) { Value = "Test" });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("SELECT * FROM test_table WHERE name = 'Test'");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldNotSubstitutePartialNameMatches()
	{
		using NpgsqlCommand cmd = new("SELECT * FROM test_table WHERE id = @id AND id2 = @id2");
		cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = 1 });
		cmd.Parameters.Add(new NpgsqlParameter("id2", NpgsqlDbType.Integer) { Value = 2 });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("SELECT * FROM test_table WHERE id = 1 AND id2 = 2");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldInlinePositionalParameter_WhenUnnamed()
	{
		using NpgsqlCommand cmd = new("SELECT * FROM test_table WHERE id = $1");
		cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = 7 });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("-- $1 [integer] = 7");
		result.ShouldContain("SELECT * FROM test_table WHERE id = 7");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatNullValue_AsNull()
	{
		using NpgsqlCommand cmd = new("SELECT @value");
		cmd.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Varchar) { Value = null });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("= NULL");
		result.ShouldContain("SELECT NULL");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatDbNullValue_AsNull()
	{
		using NpgsqlCommand cmd = new("SELECT @value");
		cmd.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Integer) { Value = DBNull.Value });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("= NULL");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatBooleanValue_AsTrueOrFalse()
	{
		using NpgsqlCommand cmd = new("SELECT @flagTrue, @flagFalse");
		cmd.Parameters.Add(new NpgsqlParameter("flagTrue", NpgsqlDbType.Boolean) { Value = true });
		cmd.Parameters.Add(new NpgsqlParameter("flagFalse", NpgsqlDbType.Boolean) { Value = false });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("SELECT TRUE, FALSE");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatDateTimeValue_WithTimestampCast()
	{
		using NpgsqlCommand cmd = new("SELECT @createdDate");
		DateTime date = new(2024, 5, 17, 13, 45, 30, 123, DateTimeKind.Unspecified);
		cmd.Parameters.Add(new NpgsqlParameter("createdDate", NpgsqlDbType.Timestamp) { Value = date });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("SELECT '2024-05-17 13:45:30.123000'::timestamp");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatDateTimeOffsetValue_WithTimestampTzCast()
	{
		using NpgsqlCommand cmd = new("SELECT @createdDate");
		DateTimeOffset date = new(2024, 5, 17, 13, 45, 30, 123, TimeSpan.FromHours(-5));
		cmd.Parameters.Add(new NpgsqlParameter("createdDate", NpgsqlDbType.TimestampTz) { Value = date });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("SELECT '2024-05-17 13:45:30.123000-05:00'::timestamptz");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatGuidValue_WithUuidCast()
	{
		using NpgsqlCommand cmd = new("SELECT @id");
		Guid id = Guid.Parse("11111111-2222-3333-4444-555555555555");
		cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("SELECT '11111111-2222-3333-4444-555555555555'::uuid");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatByteArrayValue_AsHexBytea()
	{
		using NpgsqlCommand cmd = new("SELECT @data");
		byte[] data = [0xDE, 0xAD, 0xBE, 0xEF];
		cmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Bytea) { Value = data });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain(@"SELECT '\xdeadbeef'::bytea");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatNumericValues_UsingInvariantCulture()
	{
		using NpgsqlCommand cmd = new("SELECT @intVal, @doubleVal, @longVal, @decimalVal");
		cmd.Parameters.Add(new NpgsqlParameter("intVal", NpgsqlDbType.Integer) { Value = 42 });
		cmd.Parameters.Add(new NpgsqlParameter("doubleVal", NpgsqlDbType.Double) { Value = 3.14 });
		cmd.Parameters.Add(new NpgsqlParameter("longVal", NpgsqlDbType.Bigint) { Value = 123456789012345L });
		cmd.Parameters.Add(new NpgsqlParameter("decimalVal", NpgsqlDbType.Numeric) { Value = 9.5m });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("SELECT 42, 3.14, 123456789012345, 9.5");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldDeclareNumericType_WithPrecisionAndScale()
	{
		using NpgsqlCommand cmd = new("SELECT @amount");
		cmd.Parameters.Add(new NpgsqlParameter("amount", NpgsqlDbType.Numeric) { Precision = 10, Scale = 2, Value = 123.45m });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("-- @amount [numeric(10,2)] = 123.45");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldDeclareNumericType_WithDefaultPrecision_WhenPrecisionIsZero()
	{
		using NpgsqlCommand cmd = new("SELECT @amount");
		cmd.Parameters.Add(new NpgsqlParameter("amount", NpgsqlDbType.Numeric) { Value = 1.5m });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("-- @amount [numeric(18,0)] = 1.5");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldInferNpgsqlDbType_WhenParameterIsNotNpgsqlParameter()
	{
		using FakeDbCommand cmd = new() { CommandText = "SELECT @value" };
		cmd.Parameters.Add(new FakeDbParameter { ParameterName = "@value", Value = 42 });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("-- @value [integer] = 42");
		result.ShouldContain("SELECT 42");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldEscapeUnmatchedType_AsQuotedString()
	{
		using FakeDbCommand cmd = new() { CommandText = "SELECT @value" };
		cmd.Parameters.Add(new FakeDbParameter { ParameterName = "@value", Value = new Uri("https://example.com") });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("'https://example.com/'");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldRenderMultipleParameters_InOrder()
	{
		using NpgsqlCommand cmd = new("SELECT * FROM test_table WHERE id = @id AND name = @name");
		cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = 1 });
		cmd.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Varchar) { Value = "Test" });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		int idIndex = result.IndexOf("-- @id", StringComparison.Ordinal);
		int nameIndex = result.IndexOf("-- @name", StringComparison.Ordinal);

		idIndex.ShouldBeGreaterThanOrEqualTo(0);
		nameIndex.ShouldBeGreaterThan(idIndex);
		result.ShouldContain("SELECT * FROM test_table WHERE id = 1 AND name = 'Test'");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldInlineStringArrayParameter_AsArrayLiteral()
	{
		using NpgsqlCommand cmd = new("SELECT * FROM t WHERE name = ANY(@names)");
		cmd.Parameters.Add(new NpgsqlParameter("names", TextArray) { Value = new[] { "it's", "a,b" } });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("SELECT * FROM t WHERE name = ANY(ARRAY['it''s', 'a,b'])");
		result.ShouldNotContain("System.String[]");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldNotNestArrayLiteral_WhenSqlWrapsPlaceholderInArray()
	{
		using NpgsqlCommand cmd = new("SELECT * FROM t WHERE id::text LIKE ANY(ARRAY[@ids])");
		cmd.Parameters.Add(new NpgsqlParameter("ids", IntegerArray) { Value = new[] { 1, 2 } });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("LIKE ANY(ARRAY[1, 2])");
		result.ShouldNotContain("ARRAY[ARRAY");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldInlineEmptyArray_AsEmptyArrayLiteral()
	{
		using NpgsqlCommand cmd = new("SELECT * FROM t WHERE name = ANY(@names)");
		cmd.Parameters.Add(new NpgsqlParameter("names", TextArray) { Value = Array.Empty<string>() });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("ANY('{}')");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatDateOnly_AsIsoDate()
	{
		using NpgsqlCommand cmd = new("SELECT * FROM t WHERE d = @d");
		cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = new DateOnly(2024, 1, 2) });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("d = '2024-01-02'::date");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldFormatEnum_AsUnderlyingNumber()
	{
		using NpgsqlCommand cmd = new("SELECT * FROM t WHERE k = @k");
		cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Integer) { Value = DayOfWeek.Friday });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("k = 5");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldPreserveMultilineValue_AndSubsequentParameters()
	{
		using NpgsqlCommand cmd = new("SELECT * FROM t WHERE a = @a AND b = @b");
		cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Text) { Value = "line1\nline2" });
		cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Integer) { Value = 9 });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("a = 'line1\nline2' AND b = 9");
	}

	[Fact]
	public void RenderCommandAsScript_ShouldNotReSubstitute_PlaceholderTextInsideValue()
	{
		using NpgsqlCommand cmd = new("SELECT * FROM t WHERE a = @a AND b = @b");
		cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Text) { Value = "@b" });
		cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Integer) { Value = 9 });

		string result = PostgreSqlDebug.RenderCommandAsScript(cmd);

		result.ShouldContain("a = '@b' AND b = 9");
	}
}
#endif

public sealed class PostgreSqlDebugBehaviorTests
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
	[MemberData(nameof(PostgreSqlInferredValues))]
	public void PostgreSqlDebug_WithInferredType_ShouldRenderExactLiteral(object value, string typeName, string literal)
	{
		using FakeDbCommand command = new() { CommandText = "SELECT @Value" };
		command.Parameters.Add(new FakeDbParameter { ParameterName = "Value", Value = value });

		string script = PostgreSqlDebug.RenderCommandAsScript(command);

		script.ShouldContain($"-- @Value [{typeName}] = {literal}");
		script.ShouldContain($"SELECT {literal}");
	}

	[Fact]
	public void PostgreSqlDebug_WithExplicitSizedVarchar_ShouldUseSize()
	{
		using NpgsqlCommand command = new("SELECT @Value");
		command.Parameters.Add(new NpgsqlParameter("Value", NpgsqlDbType.Varchar, 12) { Value = "abc" });

		PostgreSqlDebug.RenderCommandAsScript(command).ShouldContain("-- @Value [varchar(12)] = 'abc'");
	}

	[Fact]
	public void PostgreSqlDebug_WithScalarInsideArrayWrapper_ShouldWrapLiteralOnce()
	{
		using NpgsqlCommand command = new("SELECT ARRAY[@Value]");
		command.Parameters.Add(new NpgsqlParameter("Value", NpgsqlDbType.Integer) { Value = 7 });

		PostgreSqlDebug.RenderCommandAsScript(command).ShouldContain("SELECT ARRAY[7]");
	}

	[Fact]
	public void PostgreSqlDebug_WithEmptyArrayInsideArrayWrapper_ShouldNotNestLiteral()
	{
		using NpgsqlCommand command = new("SELECT ARRAY[@Value]");
		NpgsqlDbType textArray = (NpgsqlDbType)((int)NpgsqlDbType.Array | (int)NpgsqlDbType.Text);
		command.Parameters.Add(new NpgsqlParameter("Value", textArray) { Value = Array.Empty<string>() });

		PostgreSqlDebug.RenderCommandAsScript(command).ShouldContain("SELECT '{}'");
	}

	[Fact]
	public void PostgreSqlDebug_WithUnknownAndOverflowingPlaceholders_ShouldPreserveText()
	{
		using NpgsqlCommand command = new("SELECT @Missing, $999999999999999999999");
		command.Parameters.Add(new NpgsqlParameter("Present", NpgsqlDbType.Integer) { Value = 1 });

		PostgreSqlDebug.RenderCommandAsScript(command).ShouldContain("SELECT @Missing, $999999999999999999999");
	}

}
