using Boys.Ledger.Migrations;

var migrator = new Migrator();
migrator.EnsureDatabase();
var applied = migrator.Apply();
Console.WriteLine($"migrations applied: {applied}");
