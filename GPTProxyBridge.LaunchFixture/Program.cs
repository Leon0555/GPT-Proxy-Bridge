var directory=Environment.GetEnvironmentVariable("GPT_PB_LAUNCH_TEST_DIR");
if(string.IsNullOrEmpty(directory))return;
Directory.CreateDirectory(directory);
using var mutex=new Mutex(true,"Local\\GPTProxyBridge.Fixture."+Path.GetFileName(directory),out bool first);
if(!first){File.WriteAllText(Path.Combine(directory,"reopened"),"ok");return;}
File.WriteAllText(Path.Combine(directory,"started"),Environment.ProcessId.ToString());
Thread.Sleep(60000);
