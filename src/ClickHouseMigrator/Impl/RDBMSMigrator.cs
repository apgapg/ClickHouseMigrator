using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Ado;
using Dapper;
using Polly;
using Polly.Retry;
using Serilog;

namespace ClickHouseMigrator.Impl
{
	// ReSharper disable once InconsistentNaming
	public abstract class RDBMSMigrator
		: IMigrator
	{
		private static readonly RetryPolicy RetryPolicy = Policy.Handle<Exception>().Retry(5,
			(ex, count) => { Log.Logger.Error($"Migrate data failed [{count}]: {ex}"); });

		private static int _batch;
		private static int _counter;
		private readonly Options _options;
		private string _tableSql;
		private string _selectColumnsSql;
		private List<ColumnDefine> _primaryKeys;
		private string _insertClickHouseSql;

		protected RDBMSMigrator(Options options)
		{
			_options = options;
		}

		protected abstract string ConvertToClickHouseDataType(string type);

		protected virtual ClickHouseConnection CreateClickHouseConnection(string database = null)
		{
			var connectStr =
				$"Compress=True;CheckCompressedHash=False;Compressor=lz4;Host={_options.Host};Port={_options.Port};Database=system;User={_options.User};Password={_options.Password}";
			var settings = new ClickHouseConnectionSettings(connectStr);
			var cnn = new ClickHouseConnection(settings);
			cnn.Open();

			if (!string.IsNullOrWhiteSpace(database))
			{
				cnn.Execute($"USE {database};");
			}

			return cnn;
		}

		protected abstract Tuple<IDbCommand, int> GenerateBatchQueryCommand(IDbConnection conn,
			List<ColumnDefine> primaryKeys, string selectColumnsSql, string tableSql, int batch, int batchCount);

		protected abstract string GenerateQueryAllSql(string selectColumnsSql, string tableSql);

		protected abstract IDbConnection CreateDbConnection(string host, int port, string user, string pass,
			string database);

		protected abstract List<ColumnDefine> GetColumns(string host, int port, string user, string pass,
			string database,
			string table);

		protected abstract string GenerateTableSql(string database, string table);

		protected abstract string GenerateSelectColumnsSql(List<ColumnDefine> columns);

		public async Task RunAsync(params string[] args)
		{
			if (!Init())
			{
				return;
			}

			await PrepareClickHouse();

			Interlocked.Exchange(ref _batch, -1);
			Interlocked.Exchange(ref _counter, 0);

			Migrate();
		}

		private bool Init()
		{
			var columns = GetColumns();

			_primaryKeys = columns.Where(c => c.IsPrimary).ToList();

			if (_primaryKeys.Count == 0 && !_options.OrderBy.Any())
			{
				var msg = "Source table uncontains primary, and options uncontains order by.";
				Log.Error(msg);
				return false;
			}

			if (_options.OrderBy.Any())
			{
				foreach (var column in _options.OrderBy)
				{
					if (columns.All(cl => cl.Name.ToLower() != column.ToLower()))
					{
						var msg = $"Can't find order by column: {column} in source table.";
						Log.Error(msg);
						return false;
					}
				}
			}

			_tableSql = GenerateTableSql(_options.SourceDatabase, _options.SourceTable);

			_selectColumnsSql = GenerateSelectColumnsSql(columns);

			var insertColumnsSql = string.Join(", ",
				columns.Select(c => $"{(_options.Lowercase ? c.Name.ToLowerInvariant() : c.Name)}"));
			_insertClickHouseSql = $"INSERT INTO {_options.GetTargetTable()} ({insertColumnsSql}) VALUES @bulk;";

			return true;
		}

		private void Migrate()
		{
			if (_primaryKeys.Count <= 0 || _options.Mode.ToLower() == "sequential")
			{
				SequentialMigrate();
			}
			else
			{
				ParallelMigrate();
			}

			PrintReport();
		}

		private void PrintReport()
		{
			using var clickHouseConn = CreateClickHouseConnection(_options.GetTargetDatabase());
			var command =
				clickHouseConn.CreateCommand(
					$"SELECT COUNT(*) FROM {_options.GetTargetDatabase()}.{_options.GetTargetTable()}");
			using var reader = command.ExecuteReader();
			reader.ReadAll(x => { Log.Logger.Verbose($"Migrate {x.GetValue(0)} rows."); });
		}

		private void SequentialMigrate()
		{
			var progressWatch = new Stopwatch();
			progressWatch.Start();
			using (var clickHouseConn = CreateClickHouseConnection(_options.GetTargetDatabase()))
			using (var conn = CreateDbConnection(_options.SourceHost, _options.SourcePort, _options.SourceUser,
				_options.SourcePassword, _options.SourceDatabase))
			{
				if (conn.State != ConnectionState.Open)
				{
					conn.Open();
				}

				var watch = new Stopwatch();

				using (var reader = conn.ExecuteReader(GenerateQueryAllSql(_selectColumnsSql, _tableSql)))
				{
					var list = new List<dynamic[]>();
					watch.Restart();

					while (reader.Read())
					{
						list.Add(reader.ToArray());
						Interlocked.Increment(ref _counter);

						if (list.Count % _options.Batch == 0)
						{
							watch.Stop();

							if (_options.Trace)
							{
								Log.Logger.Debug($"Read and convert data cost: {watch.ElapsedMilliseconds} ms.");
							}

							TracePerformance(watch, () => InsertDataToClickHouse(clickHouseConn, list),
								"Insert data to clickhouse cost: {0} ms.");
							list.Clear();
							var costTime = progressWatch.ElapsedMilliseconds / 1000;
							if (costTime > 0)
							{
								Log.Logger.Verbose($"Total: {_counter}, Speed: {_counter / costTime} Row/Sec.");
							}

							watch.Restart();
						}
					}

					if (list.Count > 0)
					{
						InsertDataToClickHouse(clickHouseConn, list);
					}

					list.Clear();
				}
			}

			var finalCostTime = progressWatch.ElapsedMilliseconds / 1000;
			Log.Logger.Verbose($"Total: {_counter}, Speed: {_counter / finalCostTime} Row/Sec.");
		}

		private void ParallelMigrate()
		{
			var progressWatch = new Stopwatch();
			progressWatch.Start();

			var thread = _options.Thread;
			if (_primaryKeys.Count == 0 && _options.Thread > 1)
			{
				thread = 1;
				Log.Warning($"Table: {_options.SourceTable} contains no primary key, can't support parallel mode.");
			}

			Log.Logger.Verbose($"Thread: {_options.Thread}, Batch: {_options.Batch}.");
			Parallel.For(0, thread, new ParallelOptions {MaxDegreeOfParallelism = _options.Thread}, (i) =>
			{
				using var clickHouseConn = CreateClickHouseConnection(_options.GetTargetDatabase());
				using var conn = CreateDbConnection(_options.SourceHost, _options.SourcePort, _options.SourceUser,
					_options.SourcePassword, _options.SourceDatabase);
				if (conn.State != ConnectionState.Open)
				{
					conn.Open();
				}

				var watch = new Stopwatch();

				while (true)
				{
					Interlocked.Increment(ref _batch);

					var command = TracePerformance(watch,
						() => GenerateBatchQueryCommand(conn, _primaryKeys, _selectColumnsSql, _tableSql, _batch,
							_options.Batch), "Construct query data command cost: {0} ms.");
					if (command.Item2 == 0)
					{
						Log.Logger.Information($"Thread {i} exit.");
						break;
					}

					using (var reader = TracePerformance(watch, () => command.Item1.ExecuteReader(),
						"Query data from source database cost: {0} ms."))
					{
						var count = 0;
						var rows = TracePerformance(watch, () =>
						{
							var list = new List<dynamic[]>();

							while (reader.Read())
							{
								list.Add(reader.ToArray());
								count = Interlocked.Increment(ref _counter);
							}

							return list;
						}, "Read and convert data cost: {0} ms.");
						TracePerformance(watch, () => InsertDataToClickHouse(clickHouseConn, rows),
							"Insert data to clickhouse cost: {0} ms.");
						rows.Clear();

						if (count % _options.Batch == 0)
						{
							var costTime = progressWatch.ElapsedMilliseconds / 1000;
							if (costTime > 0)
							{
								Log.Logger.Verbose($"Total: {_counter}, Speed: {_counter / costTime} Row/Sec.");
							}
						}
					}

					if (command.Item2 < _options.Batch)
					{
						Log.Logger.Information($"Thread {i} exit.");
						break;
					}
				}
			});
			var finalCostTime = progressWatch.ElapsedMilliseconds / 1000;
			Log.Logger.Verbose(
				$"Total: {_counter}, Speed: {(finalCostTime == 0 ? 0 : (_counter / finalCostTime))} Row/Sec.");
		}

		private List<ColumnDefine> GetColumns()
		{
			return GetColumns(_options.SourceHost, _options.SourcePort, _options.SourceUser, _options.SourcePassword,
				_options.SourceDatabase, _options.SourceTable);
		}

		private T TracePerformance<T>(Stopwatch watch, Func<T> func, string message)
		{
			if (!_options.Trace || watch == null)
			{
				return func();
			}

			watch.Restart();
			var t = func();
			watch.Stop();
			Log.Logger.Debug(string.Format(message, watch.ElapsedMilliseconds));
			return t;
		}

		private void TracePerformance(Stopwatch watch, Action action, string message)
		{
			if (!_options.Trace || watch == null)
			{
				action();
				return;
			}

			watch.Restart();
			action();
			watch.Stop();
			Log.Logger.Debug(string.Format(message, watch.ElapsedMilliseconds));
		}

		private void InsertDataToClickHouse(ClickHouseConnection clickHouseConn, List<dynamic[]> list)
		{
			if (list == null || list.Count == 0)
			{
				return;
			}

			RetryPolicy.ExecuteAndCapture(() =>
			{
				using var command = clickHouseConn.CreateCommand();
				command.CommandText = _insertClickHouseSql;
				command.Parameters.Add(new ClickHouseParameter
				{
					ParameterName = "bulk",
					Value = list
				});
				//Console.WriteLine(_insertClickHouseSql);
				command.ExecuteNonQuery();
			});
		}

		private async Task PrepareClickHouse()
		{
			using var conn = CreateClickHouseConnection();
			await conn.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS {_options.GetTargetDatabase()};");
			await conn.ExecuteAsync($"USE {_options.GetTargetDatabase()};");

			if (_options.Drop)
			{
				await conn.ExecuteAsync($"DROP TABLE IF EXISTS {_options.GetTargetTable()};");
			}

			await conn.ExecuteAsync(GenerateCreateClickHouseTableSql());
		}

		private string GenerateCreateClickHouseTableSql()
		{
			var stringBuilder = new StringBuilder($"CREATE TABLE IF NOT EXISTS {_options.GetTargetTable()} (");

			foreach (var column in GetColumns())
			{
				var clickhouseDataType = ConvertToClickHouseDataType(column.DataType);
				stringBuilder.Append(
					$"{(_options.Lowercase ? column.Name.ToLowerInvariant() : column.Name)} {clickhouseDataType}, ");
			}

			stringBuilder.Remove(stringBuilder.Length - 2, 2);
			var orderBy = _options.OrderBy.Any()
				? string.Join(", ", _options.OrderBy.Select(k => _options.Lowercase ? k.ToLowerInvariant() : k))
				: string.Join(", ", _primaryKeys.Select(k => _options.Lowercase ? k.Name.ToLowerInvariant() : k.Name));

			stringBuilder.Append($") ENGINE = MergeTree() ORDER BY ({orderBy}) SETTINGS index_granularity = 8192");

			var sql = stringBuilder.ToString();
			return sql;
		}
	}
}