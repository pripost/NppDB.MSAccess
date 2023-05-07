﻿using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Data;
using System.Data.OleDb;
using NppDB.Comm;
using System.IO;
using Antlr4.Runtime;

namespace NppDB.MSAccess
{
    public class MSAccessLexerErrorListener : ConsoleErrorListener<int>
    {
        public new static readonly MSAccessLexerErrorListener Instance = new MSAccessLexerErrorListener();

        public override void SyntaxError(TextWriter output, IRecognizer recognizer, 
            int offendingSymbol, int line, int col, string msg, RecognitionException e)
        {
            Console.WriteLine($"LEXER ERROR: {e?.GetType().ToString() ?? ""}: {msg} ({line}:{col})");
        }
    }

    public class MSAccessParserErrorListener : BaseErrorListener
    {
        private readonly IList<ParserError> _errors = new List<ParserError>();
        public IList<ParserError> Errors => _errors;

        public override void SyntaxError(TextWriter output, IRecognizer recognizer, 
            IToken offendingSymbol, int line, int col, string msg, RecognitionException e)
        {
            Console.WriteLine($"PARSER ERROR: {e?.GetType().ToString() ?? ""}: {msg} ({line}:{col})");
            _errors.Add(new ParserError
            {
                Text = msg,
                StartLine = line,
                StartColumn = col,
                StartOffset = offendingSymbol.StartIndex,
                // StopLine = ,
                // StopColumn = ,
                StopOffset = offendingSymbol.StopIndex,
            }); 
        }
    }

    public class MSAccessExecutor : ISQLExecutor
    {
        private Thread _execTh;
        private readonly Func<OleDbConnection> _connector;

        public MSAccessExecutor(Func<OleDbConnection> connector)
        {
            _connector = connector;
        }

        public virtual ParserResult Parse(string sqlText, CaretPosition caretPosition)
        {
            var input = CharStreams.fromString(sqlText);

            var lexer = new MSAccessLexer(input);
            lexer.RemoveErrorListeners();
            lexer.AddErrorListener(MSAccessLexerErrorListener.Instance);

            CommonTokenStream tokens;
            try
            {
                tokens = new CommonTokenStream(lexer);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Lexer Exception: {e}");
                throw e;
            }

            var parserErrorListener = new MSAccessParserErrorListener();
            var parser = new MSAccessParser(tokens);
            parser.RemoveErrorListeners();
            parser.AddErrorListener(parserErrorListener);
            try
            {
                var tree = parser.parse();
                var enclosingCommandIndex = tree.CollectCommands(caretPosition, " ", MSAccessParser.SCOL, out var commands);
                return new ParserResult
                {
                    Errors = parserErrorListener.Errors, 
                    Commands = commands.ToList<ParsedCommand>(), 
                    EnclosingCommandIndex = enclosingCommandIndex
                };
            }
            catch (Exception e)
            {
                Console.WriteLine($"Parser Exception: {e}");
                throw e;
            }
        }

        public virtual void Execute(IList<string> sqlQueries, Action<IList<CommandResult>> callback)
        {
            _execTh = new Thread(new ThreadStart(
                delegate
                {
                    var results = new List<CommandResult>();
                    string lastSql = null;
                    try
                    {
                        using (var conn = _connector())
                        {
                            conn.Open();
                            foreach (var sql in sqlQueries)
                            {
                                if (string.IsNullOrWhiteSpace(sql)) continue;
                                lastSql = sql;

                                Console.WriteLine($"SQL: <{sql}>");
                                var cmd = new OleDbCommand(sql, conn);
                                var rd = cmd.ExecuteReader();
                                var dt = new DataTable();
                                dt.Load(rd);
                                results.Add(new CommandResult {CommandText = sql, QueryResult = dt, RecordsAffected = rd.RecordsAffected});
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add(new CommandResult {CommandText = lastSql, Error = ex});
                        callback(results);
                        return;
                    }
                    callback(results);
                    _execTh = null;
                }));
            _execTh.IsBackground = true;
            _execTh.Start();
        }

        public virtual bool CanExecute()
        {
            return !CanStop();
        }

        public virtual void Stop()
        {
            if (!CanStop()) return;
            if (_execTh != null) _execTh.Abort();
            _execTh = null;
        }

        public virtual bool CanStop()
        {
            return _execTh != null && (_execTh.ThreadState & ThreadState.Running) != 0;
        }

    }
}
