using DesktopTestDriver;

try
{
    Wire.ConfigureConsole();
    if (args.Length != 2 || args[0] != "--run") throw new DriverException(Error.InvalidRequest);
    var store = new RunStore(args[1]);
    var client = store.Read<ProcessIdentity>("client.json");
    return await WatchdogRunner.Run(store, client, new WindowsInput(), () => WindowsDesktop.ConsoleValid(store.Config.TargetSession)) ? 0 : 1;
}
catch (Exception e)
{
    Console.Error.WriteLine(Wire.Encode(new { errorCode = e is DriverException d ? d.Code : Error.CleanupIncomplete }));
    return 1;
}
