using System.CommandLine;
using Cimian.CLI.Pkginspector.Services;
using Cimian.Msi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cimian.CLI.Pkginspector;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("pkginspector - MSI and package inspector for Cimian");

        var verboseOption = new Option<bool>(["--verbose", "-v"], "Enable verbose output");
        rootCommand.AddGlobalOption(verboseOption);

        // inspect command
        var inspectCommand = new Command("inspect", "Inspect a package and show metadata summary");
        var fileArg = new Argument<string>("file", "Path to the .msi file");
        var yamlOption = new Option<bool>("--yaml", "Output as YAML");
        var jsonOption = new Option<bool>("--json", "Output as JSON");
        inspectCommand.AddArgument(fileArg);
        inspectCommand.AddOption(yamlOption);
        inspectCommand.AddOption(jsonOption);
        inspectCommand.SetHandler((file, yaml, json, verbose) =>
        {
            var inspector = CreateInspector(verbose);
            inspector.Inspect(file, yaml, json);
        }, fileArg, yamlOption, jsonOption, verboseOption);

        // tables command
        var tablesCommand = new Command("tables", "List all tables in an MSI database");
        var tablesFileArg = new Argument<string>("file", "Path to the .msi file");
        tablesCommand.AddArgument(tablesFileArg);
        tablesCommand.SetHandler((file, verbose) =>
        {
            var inspector = CreateInspector(verbose);
            inspector.ListTables(file);
        }, tablesFileArg, verboseOption);

        // table command
        var tableCommand = new Command("table", "Dump contents of a specific MSI table");
        var tableFileArg = new Argument<string>("file", "Path to the .msi file");
        var tableNameArg = new Argument<string>("name", "Table name to dump");
        tableCommand.AddArgument(tableFileArg);
        tableCommand.AddArgument(tableNameArg);
        tableCommand.SetHandler((file, name, verbose) =>
        {
            var inspector = CreateInspector(verbose);
            inspector.DumpTable(file, name);
        }, tableFileArg, tableNameArg, verboseOption);

        // files command
        var filesCommand = new Command("files", "List all files in the MSI File table");
        var filesFileArg = new Argument<string>("file", "Path to the .msi file");
        filesCommand.AddArgument(filesFileArg);
        filesCommand.SetHandler((file, verbose) =>
        {
            var inspector = CreateInspector(verbose);
            inspector.ListFiles(file);
        }, filesFileArg, verboseOption);

        // verify command
        var verifyCommand = new Command("verify", "Validate MSI structure");
        var verifyFileArg = new Argument<string>("file", "Path to the .msi file");
        verifyCommand.AddArgument(verifyFileArg);
        verifyCommand.SetHandler((file, verbose) =>
        {
            var inspector = CreateInspector(verbose);
            inspector.Verify(file);
        }, verifyFileArg, verboseOption);

        // receipt command
        var receiptCommand = new Command("receipt", "Show installed receipt by identifier");
        var receiptIdArg = new Argument<string>("identifier", "Product identifier");
        receiptCommand.AddArgument(receiptIdArg);
        receiptCommand.SetHandler((identifier, verbose) =>
        {
            var inspector = CreateInspector(verbose);
            inspector.ShowReceipt(identifier);
        }, receiptIdArg, verboseOption);

        rootCommand.AddCommand(inspectCommand);
        rootCommand.AddCommand(tablesCommand);
        rootCommand.AddCommand(tableCommand);
        rootCommand.AddCommand(filesCommand);
        rootCommand.AddCommand(verifyCommand);
        rootCommand.AddCommand(receiptCommand);

        return await rootCommand.InvokeAsync(args);
    }

    private static MsiInspector CreateInspector(bool verbose)
    {
        var reader = new MsiPropertyReader(NullLogger<MsiPropertyReader>.Instance);
        var receiptManager = new MsiReceiptManager(NullLogger<MsiReceiptManager>.Instance);
        return new MsiInspector(reader, receiptManager);
    }
}
