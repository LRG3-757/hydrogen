namespace hydrogen;

static class Program
{
    /// <summary>
    /// 应用程序的主入口点。
    /// </summary>
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        // 原来：Application.Run(new hydrogen());
        // 修改为（窗体类改名 HydrogenForm）
        Application.Run(new hydrogen());

    }
}
