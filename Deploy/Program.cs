using System.Diagnostics;
using System.Net.Http;

const string Url = "http://localhost:5257/";

var baseDir = AppContext.BaseDirectory;
var runDir = Path.Combine(baseDir, "ManpowerManagement-Run");
var exe = Path.Combine(runDir, "ManpowerManagement.exe");

if (!File.Exists(exe))
{
    Console.WriteLine($"ไม่พบไฟล์ {exe}");
    Console.WriteLine("Publish ก่อน: dotnet publish ManpowerManagement -c Release -o ManpowerManagement-Run");
    Pause();
    return 1;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

async Task<bool> IsUpAsync()
{
    try { return (await http.GetAsync(Url)).IsSuccessStatusCode; }
    catch { return false; }
}

if (await IsUpAsync())
{
    Console.WriteLine("Web app กำลังทำงานอยู่แล้ว");
}
else
{
    Console.WriteLine("กำลังเริ่ม Manpower Management ...");
    Process.Start(new ProcessStartInfo(exe)
    {
        WorkingDirectory = runDir,
        UseShellExecute = true,
        WindowStyle = ProcessWindowStyle.Hidden,
    });

    var up = false;
    for (var i = 0; i < 60 && !up; i++)
    {
        await Task.Delay(1000);
        up = await IsUpAsync();
    }
    if (!up)
    {
        Console.WriteLine("เริ่ม web app ไม่สำเร็จ (ดู log ใน ManpowerManagement-Run)");
        Pause();
        return 1;
    }
}

Console.WriteLine($"เปิด {Url}");
Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
return 0;

static void Pause()
{
    Console.WriteLine("กด Enter เพื่อปิด");
    Console.ReadLine();
}
