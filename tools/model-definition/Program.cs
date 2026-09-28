using ModelDefinition;

try
{
    if (args.Length is < 1 or > 2 || args[0] is not ("validate" or "test" or "package"))
    {
        Console.Error.WriteLine("Usage: ModelDefinition <validate|test|package> [project-root]");
        return 2;
    }
    var root = Path.GetFullPath(args.Length == 2 ? args[1] : Directory.GetCurrentDirectory());
    var model = ModelValidator.Load(root);
    if (args[0] == "test")
        Console.WriteLine($"{OfflineTests.Run(root, model)} offline tests passed.");
    else if (args[0] == "package")
    {
        var package = DefinitionPackage.Build(model);
        Console.WriteLine("Approval SHA256: " + package.ApprovalHash);
        Console.WriteLine("Files: " + DefinitionPackage.Publish(root, package));
    }
    else
        Console.WriteLine("PASS: genuine TOM TMDL parsing and round-trip; exact fixture, relationship and role metadata checks.");
    Console.WriteLine("NOT live validated: DAX/M execution, service acceptance, identity propagation and RLS enforcement remain unproven.");
    Console.WriteLine("No deployment or authentication was attempted.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
