using Elio.Core;

namespace Elio.Tests;

public sealed class HostImportTests
{
    private const string Ini = """
        # A small estate.
        mail.example.com
        bastion ansible_host=203.0.113.9 ansible_port=2222 ansible_user="ops team"   # the way in

        [web]
        web[01:03].example.com ansible_user=deploy
        legacy.example.com:2200

        [db]
        db-[a:b].internal ansible_ssh_private_key_file=/keys/db.pem
        web02.example.com

        [db:vars]
        ansible_user=postgres
        ansible_port=5022

        [prod:children]
        web
        db

        [prod:vars]
        ansible_user=root
        ansible_port=2022

        [all:vars]
        ansible_ssh_private_key_file=/keys/all.pem
        """;

    private const string Yaml = """
        ---
        # The same estate, the other way of writing it.
        all:
          vars:
            ansible_ssh_private_key_file: /keys/all.pem
            ntp_server: "ntp: one"   # a quoted value may hold a colon
          hosts:
            mail.example.com:
            bastion:
              ansible_host: 203.0.113.9
              ansible_port: 2222
              ansible_user: 'ops team'
          children:
            prod:
              vars:
                ansible_user: root
                ansible_port: 2022
              children:
                web:
                  hosts:
                    web[01:03].example.com:
                      ansible_user: deploy
                    legacy.example.com:
                      ansible_port: 2200
                db:
                  vars:
                    ansible_user: postgres
                    ansible_port: 5022
                  hosts:
                    db-[a:b].internal:
                      ansible_ssh_private_key_file: /keys/db.pem
                    web02.example.com:
        """;

    private static HostEntry Host(HostLibrary library, string name) => Assert.Single(library.Hosts, h => h.Name == name);

    private static string? GroupPath(HostLibrary library, Guid? id)
    {
        if (id is null) return null;
        HostGroup group = Assert.Single(library.Groups, g => g.Id == id);
        return group.ParentId is null ? group.Name : GroupPath(library, group.ParentId) + "/" + group.Name;
    }

    public static TheoryData<string, string> Inventories() => new() { { "ini", Ini }, { "yaml", Yaml } };

    [Theory]
    [MemberData(nameof(Inventories))]
    public void AnsibleInventory_EitherForm_GivesTheSameServersAndGroups(string form, string text)
    {
        HostLibrary library = form == "ini" ? HostImport.FromAnsibleIni(text) : HostImport.FromAnsibleYaml(text);

        Assert.Equal(
            ["bastion", "db-a.internal", "db-b.internal", "legacy.example.com", "mail.example.com", "web01.example.com", "web02.example.com", "web03.example.com"],
            library.Hosts.Select(h => h.Name).Order(StringComparer.Ordinal));

        // Children are nested: web and db sit inside prod, and nothing is made for "all" or "ungrouped".
        Assert.Equal(["prod", "prod/db", "prod/web"], library.Groups.Select(g => GroupPath(library, g.Id)).Order(StringComparer.Ordinal));

        HostEntry mail = Host(library, "mail.example.com");
        Assert.Equal(("mail.example.com", 22, Environment.UserName, null), (mail.Address, mail.Port, mail.User, GroupPath(library, mail.GroupId)));
        Assert.Equal("/keys/all.pem", mail.KeyPath);
        Assert.False(mail.UseAgent);

        HostEntry bastion = Host(library, "bastion");
        Assert.Equal(("203.0.113.9", 2222, "ops team"), (bastion.Address, bastion.Port, bastion.User));

        // The host's own user beats every group's. The port comes from prod, the group above the one it is in.
        HostEntry web1 = Host(library, "web01.example.com");
        Assert.Equal(("web01.example.com", 2022, "deploy", "prod/web"), (web1.Address, web1.Port, web1.User, GroupPath(library, web1.GroupId)));
        Assert.Empty(web1.Tags);

        Assert.Equal(2200, Host(library, "legacy.example.com").Port);

        HostEntry db = Host(library, "db-b.internal");
        Assert.Equal((5022, "postgres", "/keys/db.pem", "prod/db"), (db.Port, db.User, db.KeyPath, GroupPath(library, db.GroupId)));

        // In two groups there, in the first of them here with the second as a tag. Its own "deploy" still wins.
        HostEntry web2 = Host(library, "web02.example.com");
        Assert.Equal("prod/web", GroupPath(library, web2.GroupId));
        Assert.Equal(["db"], web2.Tags);
        Assert.Equal("deploy", web2.User);
    }

    [Theory]
    [InlineData("inventory.yml", true)]
    [InlineData("inventory.yaml", true)]
    [InlineData("hosts.ini", false)]
    [InlineData("hosts", false)]
    [InlineData(null, false)]
    public void AnsibleInventory_ChoosesTheFormByNameAndThenByContent(string? fileName, bool yaml)
    {
        HostLibrary library = HostImport.FromAnsibleInventory(yaml ? Yaml : Ini, fileName);
        Assert.Equal(8, library.Hosts.Count);

        // With no name to go by, the YAML one is still recognised by how it starts.
        Assert.Equal(8, HostImport.FromAnsibleInventory(Yaml).Hosts.Count);
    }

    [Theory]
    [InlineData("web[1:3]", "web1,web2,web3")]
    [InlineData("web[08:10].x", "web08.x,web09.x,web10.x")]
    [InlineData("n[1:7:3]", "n1,n4,n7")]
    [InlineData("r[a:c]", "ra,rb,rc")]
    [InlineData("r[a:b]-[1:2]", "ra-1,ra-2,rb-1,rb-2")]
    [InlineData("plain.example.com", "plain.example.com")]
    public void AnsibleIni_ExpandsRanges(string pattern, string expected) =>
        Assert.Equal(expected.Split(','), HostImport.FromAnsibleIni(pattern).Hosts.Select(h => h.Name));

    [Theory]
    [InlineData("[web]\nweb[3:1]", 2)]
    [InlineData("[web]\nweb[1:x]", 2)]
    [InlineData("[web]\nweb[1:999999999]", 2)]
    [InlineData("[web\nhost", 1)]
    [InlineData("[web:hosts]\nhost", 1)]
    [InlineData("host\nother stray", 2)]
    [InlineData("[web:vars]\nno equals sign", 2)]
    [InlineData("[prod:children]\nweb db", 2)]
    [InlineData("host ansible_user=\"unclosed", 1)]
    public void AnsibleIni_WhatItDoesNotUnderstand_FailsWithTheLine(string text, int line)
    {
        var error = Assert.Throws<ImportFormatException>(() => HostImport.FromAnsibleIni(text));
        Assert.Equal(line, error.Line);
        Assert.Contains(line.ToString(), error.Message);
    }

    [Theory]

    // Lists, flow collections, anchors, aliases, tags, block text, a second document.
    [InlineData("all:\n  hosts:\n    - web1", 3)]
    [InlineData("all:\n  hosts: {web1: }", 2)]
    [InlineData("all:\n  vars:\n    dns: [a, b]", 3)]
    [InlineData("all:\n  vars: &shared\n    a: b", 2)]
    [InlineData("all:\n  vars:\n    a: *shared", 3)]
    [InlineData("all:\n  vars:\n    secret: !vault |\n      abc", 3)]
    [InlineData("all:\n  vars:\n    motd: |\n      hello", 3)]
    [InlineData("all:\n  vars:\n    motd: >\n      hello", 3)]
    [InlineData("all:\n  hosts:\n    web1:\n---\nall:", 4)]
    [InlineData("all:\n  vars:\n    a: \"runs on", 3)]

    // Indentation that does not line up, tabs, a value that goes on, and a repeated key.
    [InlineData("all:\n    hosts:\n  vars:", 3)]
    [InlineData("all:\n  hosts:\n      web1:\n    web2:", 4)]
    [InlineData("all:\n\thosts:", 2)]
    [InlineData("all:\n  vars:\n    a: one\n      two", 4)]
    [InlineData("all:\n  hosts:\n  hosts:", 3)]

    // Valid YAML that is not an inventory.
    [InlineData("all:\n  servers:\n    web1:", 2)]
    [InlineData("all:\n  hosts: web1", 2)]
    [InlineData("all:\n  hosts:\n    web1: up", 3)]
    [InlineData("all: everything", 1)]
    [InlineData("just some text", 1)]
    [InlineData("all:\n  vars:\n    a: b: c", 3)]
    [InlineData("all:\n  hosts:\n    web[1:x]:", 3)]
    public void AnsibleYaml_WhatItDoesNotUnderstand_FailsWithTheLineRatherThanGuess(string text, int line)
    {
        var error = Assert.Throws<ImportFormatException>(() => HostImport.FromAnsibleYaml(text));
        Assert.Equal(line, error.Line);
        Assert.Contains(line.ToString(), error.Message);
    }

    [Fact]
    public void AnsibleYaml_ReadsQuotedKeysCommentsNullsAndIPv6()
    {
        const string text = """
            web:            # a group at the top level, with no "all" around it
              hosts:
                "quoted name":
                  ansible_host: 'it''s.example.com'   # two quotes are one
                  ansible_user: ~
                  ansible_port: "2201"
                v6:
                  ansible_host: fe80::1
                templated:
                  ansible_host: "{{ lookup('env', 'TARGET') }}"
                localhost:
                  ansible_connection: local
              vars:
                settings:
                  nested: value
                ansible_user: null
            """;

        HostLibrary library = HostImport.FromAnsibleYaml(text);

        Assert.Equal(["quoted name", "v6", "templated"], library.Hosts.Select(h => h.Name));
        Assert.Equal(("it's.example.com", 2201, Environment.UserName), (library.Hosts[0].Address, library.Hosts[0].Port, library.Hosts[0].User));
        Assert.Equal("fe80::1", library.Hosts[1].Address);

        // What only Ansible can work out is left as the name, not copied in as an address.
        Assert.Equal("templated", library.Hosts[2].Address);
        Assert.True(library.Hosts[2].UseAgent);
        Assert.All(library.Hosts, h => Assert.Equal("web", GroupPath(library, h.GroupId)));
    }

    [Fact]
    public void AnsibleIni_AGroupThatIsItsOwnAncestor_StillImports()
    {
        HostLibrary library = HostImport.FromAnsibleIni("[a]\nhost1\n[a:children]\nb\n[b:children]\na\n[b:vars]\nansible_port=9");

        Assert.Equal(9, Assert.Single(library.Hosts).Port);
        Assert.NotNull(library.Hosts[0].GroupId);
    }

    [Fact]
    public void Csv_ReadsQuotedFieldsSynonymsAndNestedGroups()
    {
        const string text =
            "Name,IP Address,Port,User Name,Group,Tags,Notes\r\n" +
            "\"web, front\",10.0.0.1,2222,deploy,prod/web,\"blue, eu\",\"said \"\"hello\"\"\r\nand left\"\r\n" +
            "\r\n" +
            "db,10.0.0.2,,,prod,single,\r\n" +
            ",10.0.0.3,not a port,root,,,\r\n" +
            "no address,,22,root,,,\r\n";

        HostLibrary library = HostImport.FromCsv(text);

        Assert.Equal(3, library.Hosts.Count);

        HostEntry web = library.Hosts[0];
        Assert.Equal(("web, front", "10.0.0.1", 2222, "deploy", "prod/web"), (web.Name, web.Address, web.Port, web.User, GroupPath(library, web.GroupId)));
        Assert.Equal(["blue", "eu"], web.Tags);

        HostEntry db = library.Hosts[1];
        Assert.Equal(("db", 22, Environment.UserName, "prod"), (db.Name, db.Port, db.User, GroupPath(library, db.GroupId)));
        Assert.Equal(["single"], db.Tags);

        // Without a name the address stands in for it, and a port that is not a number is the usual one.
        Assert.Equal(("10.0.0.3", 22, "root", null), (library.Hosts[2].Name, library.Hosts[2].Port, library.Hosts[2].User, library.Hosts[2].GroupId));

        // "prod" was made once, though two rows name it.
        Assert.Equal(["prod", "prod/web"], library.Groups.Select(g => GroupPath(library, g.Id)).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("hostname\tlogin\tssh_port\tfolder\tlabels\nbox.example.com\tadmin\t2022\tlab\ta|b\n")]
    [InlineData("hostname;login;ssh_port;folder;labels\nbox.example.com;admin;2022;lab;\"a;b\"\n")]
    [InlineData("﻿hostname,login,ssh_port,folder,labels\n  box.example.com , admin ,2022,lab,\"a,b\"")]
    public void Csv_TakesTabsSemicolonsAndCommas(string text)
    {
        HostLibrary library = HostImport.FromCsv(text);

        HostEntry box = Assert.Single(library.Hosts);
        Assert.Equal(("box.example.com", "box.example.com", "admin", 2022), (box.Name, box.Address, box.User, box.Port));
        Assert.Equal(["a", "b"], box.Tags);
        Assert.Equal("lab", Assert.Single(library.Groups).Name);
    }

    [Fact]
    public void Csv_HostnameBesideAnAddressColumn_IsTheName()
    {
        HostEntry host = Assert.Single(HostImport.FromCsv("hostname,ip\nweb,10.1.1.1\n").Hosts);
        Assert.Equal(("web", "10.1.1.1"), (host.Name, host.Address));
    }

    [Theory]
    [InlineData("")]
    [InlineData("name,port\nweb,22\n")]
    [InlineData("name,host\n\"web,10.0.0.1\n")]
    public void Csv_WithoutAnAddressColumnOrWithAnOpenQuote_Fails(string text) =>
        Assert.Throws<ImportFormatException>(() => HostImport.FromCsv(text));
}
