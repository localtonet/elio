using Elio.SyncServer;

// Configuration: Sync:PublicUrl (required), Sync:Localtonet, Sync:StaticTokens, Sync:Database, Sync:MaxBytes,
// Sync:MaxVaults, Sync:VaultIdleDays. Behind a reverse proxy: Sync:BehindProxy and Sync:TrustedProxies.
// The relay for shared terminals: Sync:ShareRelay (on unless set to false) and the Sync:Share... limits in SyncServerOptions.
// As environment variables: Sync__PublicUrl=https://sync.example.com and so on.
WebApplication app = SyncServerApp.Build(args);
await app.RunAsync();
