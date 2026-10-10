namespace Jularr.Infrastructure.Sql;

internal static class SqlReadGuard
{
    private static readonly HashSet<string> ForbiddenCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALTER", "CALL", "COMMENT", "COPY", "CREATE", "DELETE", "DO", "DROP", "EXECUTE",
        "GRANT", "INSERT", "INTO", "LISTEN", "LOCK", "MERGE", "NOTIFY", "REFRESH",
        "RESET", "REVOKE", "SET", "TRUNCATE", "UNLISTEN", "UPDATE"
    };

    internal static void RequireReadStatement(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var tokens = Tokenize(sql);
        if (tokens.Count == 0 || tokens[0] is not ("SELECT" or "WITH" or "SHOW"))
        {
            throw new InvalidOperationException("ReadSql accepts SELECT/WITH queries and read-only SHOW diagnostics only.");
        }

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (ForbiddenCommands.Contains(token))
            {
                throw new InvalidOperationException($"ReadSql cannot execute the '{token}' command.");
            }

            if (token == "FOR" && index + 1 < tokens.Count && tokens[index + 1] is "UPDATE" or "SHARE" or "KEY" or "NO")
            {
                throw new InvalidOperationException("ReadSql cannot acquire explicit row write locks.");
            }

            if (token == ";" && index != tokens.Count - 1)
            {
                throw new InvalidOperationException("ReadSql cannot execute multiple statements.");
            }
        }
    }

    private static List<string> Tokenize(string sql)
    {
        var tokens = new List<string>();

        for (var index = 0; index < sql.Length;)
        {
            if (sql[index] == '\'')
            {
                var escaped = index > 0 && sql[index - 1] is 'E' or 'e' && (index == 1 || !IsNameChar(sql[index - 2]));
                index++;
                var closed = false;
                while (index < sql.Length)
                {
                    if (escaped && sql[index] == '\\')
                    {
                        index = Math.Min(index + 2, sql.Length);
                    }
                    else if (sql[index] == '\'')
                    {
                        if (index + 1 < sql.Length && sql[index + 1] == '\'')
                        {
                            index += 2;
                        }
                        else
                        {
                            index++;
                            closed = true;
                            break;
                        }
                    }
                    else
                    {
                        index++;
                    }
                }

                if (!closed)
                {
                    throw new InvalidOperationException("Unterminated SQL string literal.");
                }

                continue;
            }

            if (sql[index] == '"')
            {
                index++;
                var closed = false;
                while (index < sql.Length)
                {
                    if (sql[index] == '"')
                    {
                        if (index + 1 < sql.Length && sql[index + 1] == '"')
                        {
                            index += 2;
                        }
                        else
                        {
                            index++;
                            closed = true;
                            break;
                        }
                    }
                    else
                    {
                        index++;
                    }
                }

                if (!closed)
                {
                    throw new InvalidOperationException("Unterminated quoted SQL identifier.");
                }

                continue;
            }

            if (index + 1 < sql.Length && sql[index] == '-' && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                var depth = 1;
                index += 2;
                while (index < sql.Length && depth > 0)
                {
                    if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
                    {
                        depth++;
                        index += 2;
                    }
                    else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
                    {
                        depth--;
                        index += 2;
                    }
                    else
                    {
                        index++;
                    }
                }

                if (depth != 0)
                {
                    throw new InvalidOperationException("Unterminated SQL comment.");
                }

                continue;
            }

            if (sql[index] == '$')
            {
                var delimiterEnd = index + 1;
                while (delimiterEnd < sql.Length && IsNameChar(sql[delimiterEnd]))
                {
                    delimiterEnd++;
                }

                if (delimiterEnd < sql.Length && sql[delimiterEnd] == '$')
                {
                    var delimiter = sql[index..(delimiterEnd + 1)];
                    var end = sql.IndexOf(delimiter, delimiterEnd + 1, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        throw new InvalidOperationException("Unterminated dollar-quoted SQL string.");
                    }

                    index = end + delimiter.Length;
                    continue;
                }
            }

            if (IsNameStart(sql[index]))
            {
                var start = index++;
                while (index < sql.Length && IsNameChar(sql[index]))
                {
                    index++;
                }

                tokens.Add(sql[start..index].ToUpperInvariant());
                continue;
            }

            if (sql[index] == ';')
            {
                tokens.Add(";");
            }

            index++;
        }

        return tokens;
    }

    private static bool IsNameStart(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';

    private static bool IsNameChar(char value) => IsNameStart(value) || char.IsAsciiDigit(value);
}
