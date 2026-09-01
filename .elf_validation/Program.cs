using V3RttMonitor.Core.Hss;

string elfPath = Path.GetFullPath(args[0]);
var reader = new ElfSymbolReader();
ElfSymbolCatalog catalog = await reader.ReadAsync(elfPath);

Console.WriteLine($"Sections={catalog.RamSections.Count}");
foreach (ElfRamSection section in catalog.RamSections)
{
    Console.WriteLine($"  [{section.Index}] {section.Name} 0x{section.Address:X8} size={section.Size} {section.Flags}");
}

Console.WriteLine($"Objects={catalog.Symbols.Count}");
Console.WriteLine($"Scalars={catalog.Symbols.Count(x => x.IsScalarCandidate)}");
Console.WriteLine($"GlobalScalars={catalog.Symbols.Count(x => x.IsScalarCandidate && x.Binding != ElfSymbolBinding.Local)}");

IReadOnlyList<ElfSymbol> results = catalog.Search(new ElfSymbolSearchOptions
{
    SearchText = "speed",
    ScalarOnly = true,
    MaxResults = 12,
});

foreach (ElfSymbol symbol in results)
{
    Console.WriteLine($"  {symbol.Name,-28} {symbol.AddressText} size={symbol.Size} nm={symbol.NmType} kind={symbol.Kind} default={symbol.DefaultNumericType}");
}
