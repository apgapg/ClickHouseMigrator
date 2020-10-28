﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using Dapper;
using System.Linq;
using MySqlConnector;

namespace ClickHouseMigrator.Impl
{
	public class MySqlMigrator : RDBMSMigrator
	{
		private List<ColumnDefine> _columns;

		public MySqlMigrator(Options options) : base(options)
		{
		}

		protected override List<ColumnDefine> GetColumns(string host, int port, string user, string pass, string database, string table)
		{
			if (_columns == null)
			{
				using var conn = CreateDbConnection(host, port, user, pass, database);
				_columns = conn.Query($"show columns from `{database}`.`{table}`;")
					.Select(c =>
					{
						var dic = (IDictionary<string, dynamic>)c;
						return new ColumnDefine
						{
							DataType = dic["Type"],
							IsPrimary = dic["Key"] == "PRI",
							Name = dic["Field"]
						};
					}).ToList();
			}
			return _columns;
		}

		protected override string ConvertToClickHouseDataType(string type)
		{
			var sizePrefixIndex = type.IndexOf('(');
			var normalTypeName = sizePrefixIndex <= 0 ? type : type.Substring(0, sizePrefixIndex);
			switch (normalTypeName)
			{
				case "timestamp":
					{
						return "DateTime";
					}
				case "date":
					{
						return "Date";
					}
				case "tinyint":
					{
						return "UInt8";
					}
				case "smallint":
					{
						return "Int16";
					}
				case "int":
					{
						return "Int32";
					}
				case "float":
					{
						return "Float32";
					}
				case "double":
					{
						return "Float64";
					}
				case "bigint":
					{
						return "Int64";
					}
				default:
					{
						return "String";
					}
			}
		}

		protected override IDbConnection CreateDbConnection(string host, int port, string user, string pass, string database)
		{
			if (string.IsNullOrWhiteSpace(user))
			{
				user = "root";
			}
			if (port <= 0)
			{
				port = 3306;
			}
			if (string.IsNullOrWhiteSpace(database))
			{
				database = "mysql";
			}
			var passwordPart = string.IsNullOrWhiteSpace(pass) ? "" : $"Password={pass}";
			var connectString =
				$"Database='{database}';Data Source={host};User ID={user};{passwordPart};Port={port};SslMode=None;Connection Timeout=120";
			var conn = new MySqlConnection(connectString);
			return conn;
		}

		private string GetSelectPrimaryKeySql(string primaryKeysSql, string tableSql, int batch, int batchCount)
		{
			var start = batch * batchCount;
			return $"SELECT {primaryKeysSql} FROM {tableSql} LIMIT {start}, {batchCount}";
		}

		protected override Tuple<IDbCommand, int> GenerateBatchQueryCommand(IDbConnection conn, List<ColumnDefine> primaryKeys,
			string selectColumnsSql, string tableSql, int batch, int batchCount)
		{
			var primaryKeysSql = GeneratePrimaryKeysSql(primaryKeys);

			var sql = GetSelectPrimaryKeySql(primaryKeysSql, tableSql, batch, batchCount);

			var primaries = conn.Query(sql).Select(d => d as IDictionary<string, dynamic>).ToArray();
			if (primaries.Length == 0)
			{
				return new Tuple<IDbCommand, int>(null, 0);
			}

			var command = conn.CreateCommand();

			var builder = new StringBuilder();
			for (var j = 0; j < primaries.Length; ++j)
			{
				var values = primaries.ElementAt(j);
				builder.Append("(");

				for (var k = 0; k < primaryKeys.Count; ++k)
				{
					var parameterName = $"@P{j}";

					builder.Append(k == primaryKeys.Count - 1 ? $"{parameterName}" : $"{parameterName}, ");

					var parameter = command.CreateParameter();
					parameter.ParameterName = parameterName;
					parameter.Value = values[primaryKeys[k].Name];
					command.Parameters.Add(parameter);
				}

				builder.Append(j == primaries.Length - 1 ? ") " : "), ");
			}

			var inParameters = builder.Remove(builder.Length - 1, 1).ToString();

			command.CommandText = $"SELECT {selectColumnsSql} FROM {tableSql} WHERE ({primaryKeysSql}) IN ({inParameters})";

			return new Tuple<IDbCommand, int>(command, primaries.Length);
		}

		protected override string GenerateQueryAllSql(string selectColumnsSql, string tableSql)
		{
			return $"SELECT {selectColumnsSql} FROM {tableSql}";
		}

		private string GeneratePrimaryKeysSql(List<ColumnDefine> columns)
		{
			return string.Join(", ", columns.Select(k => $"`{k.Name}`"));
		}

		protected override string GenerateTableSql(string database, string table)
		{
			return $"`{database}`.`{table}`";
		}

		protected override string GenerateSelectColumnsSql(List<ColumnDefine> columns)
		{
			return string.Join(',', columns.Select(c => $"`{c.Name}`"));
		}
	}
}