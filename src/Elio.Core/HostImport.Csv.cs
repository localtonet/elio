using System.Text;

namespace Elio.Core;

/// <content>A table of servers, as a spreadsheet or another tool's export writes it: one row each, under a row of column names.</content>
public static partial class HostImport
{
    // What each column may be called, in lower case with spaces, dashes and underscores taken out.
    private static readonly string[] NameColumns = ["name", "label", "title", "alias", "displayname"];
    private static readonly string[] AddressColumns = ["address", "host", "ip", "ipaddress", "hostname", "fqdn", "server", "dns"];
    private static readonly string[] PortColumns = ["port", "sshport"];
    private static readonly string[] UserColumns = ["user", "username", "login", "account"];
    private static readonly string[] GroupColumns = ["group", "folder", "category"];
    private static readonly string[] TagColumns = ["tags", "tag", "labels"];

    /// <summary>
    /// The servers in a table with a header row. Columns are separated by commas, semicolons or tabs, whichever the
    /// header row uses. A group written as "a/b" is group b inside group a.
    /// </summary>
    /// <exception cref="ImportFormatException">There is no column for the address, or a quote is never closed.</exception>
    public static HostLibrary FromCsv(string text)
    {
        string header = text.Split('\n').FirstOrDefault(line => line.Trim().Length > 0) ?? string.Empty;
        char separator = header.Contains('\t') ? '\t' : header.Count(c => c == ';') > header.Count(c => c == ',') ? ';' : ',';

        List<(int Line, List<string> Fields)> rows = ReadRows(text, separator);
        var library = new HostLibrary();
        if (rows.Count == 0) throw MissingAddressColumn();

        List<string> columns = rows[0].Fields.Select(c => new string(c.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()).ToList();

        // The first of the names that the table has, so the likelier name wins where two columns would do.
        int Column(string[] names) => names.Select(n => columns.IndexOf(n)).FirstOrDefault(index => index >= 0, -1);

        int name = Column(NameColumns), address = Column(AddressColumns), port = Column(PortColumns);
        int user = Column(UserColumns), group = Column(GroupColumns), tags = Column(TagColumns);
        if (address < 0) throw MissingAddressColumn();

        // Beside a column of addresses, "hostname" is what the server is called rather than where it is.
        if (name < 0 && columns.IndexOf("hostname") is >= 0 and var called && called != address) name = called;

        var groups = new Dictionary<string, HostGroup>(StringComparer.Ordinal);

        // The group at the end of a path such as "prod/web", made along with the ones before it.
        Guid? Place(string path)
        {
            HostGroup? parent = null;
            string walked = string.Empty;
            foreach (string part in path.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                walked += "/" + part;
                if (!groups.TryGetValue(walked, out HostGroup? found))
                {
                    groups[walked] = found = new HostGroup { Name = part, ParentId = parent?.Id };
                    library.Groups.Add(found);
                }

                parent = found;
            }

            return parent?.Id;
        }

        foreach (var (_, fields) in rows.Skip(1))
        {
            string Cell(int column) => column >= 0 && column < fields.Count ? fields[column].Trim() : string.Empty;

            // A row without an address is a blank line or a subtotal, not a server.
            if (Cell(address) is not { Length: > 0 } where) continue;

            library.Hosts.Add(new HostEntry
            {
                Name = Cell(name) is { Length: > 0 } given ? given : where,
                Address = where,
                Port = int.TryParse(Cell(port), out int number) && number is > 0 and <= 65535 ? number : 22,
                User = Cell(user) is { Length: > 0 } login ? login : Environment.UserName,
                GroupId = Place(Cell(group)),
                Tags = Cell(tags).Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToList(),

                // Nothing in the table says how to log in, and ssh itself would try the agent first.
                UseAgent = true,
            });
        }

        return library;
    }

    private static ImportFormatException MissingAddressColumn() =>
        new(1, L.T("The first row has to name the columns, and one of them has to be the address: host, address, hostname or ip."));

    /// <summary>Splits a table into rows of fields. A field in double quotes may hold the separator, line breaks, and a quote written twice.</summary>
    private static List<(int Line, List<string> Fields)> ReadRows(string text, char separator)
    {
        var rows = new List<(int Line, List<string> Fields)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, wasQuoted = false;
        int line = 1, startedAt = 1;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            wasQuoted = false;
        }

        void EndRow()
        {
            EndField();
            if (fields.Any(f => f.Trim().Length > 0)) rows.Add((startedAt, fields));
            fields = [];
            startedAt = line + 1;
        }

        // Some programs put a mark at the very start of the file to say how it is encoded.
        for (int i = text.StartsWith('﻿') ? 1 : 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
            }
            else if (c == '"' && !wasQuoted && field.ToString().Trim().Length == 0)
            {
                field.Clear();
                quoted = wasQuoted = true;
            }
            else if (c == separator)
            {
                EndField();
            }
            else if (c == '\n')
            {
                EndRow();
                line++;
            }
            else if (c != '\r')
            {
                field.Append(c);
            }
        }

        if (quoted) throw new ImportFormatException(startedAt, L.T("Line {0}: the import does not understand this line.", startedAt));
        EndRow();
        return rows;
    }
}
