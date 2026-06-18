namespace Moongazing.OrionSaga.Demo;

/// <summary>Tiny console formatting helper so every scenario prints a consistent banner.</summary>
public static class DemoConsole
{
    public static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine(title);
        Console.WriteLine(new string('=', 70));
    }
}
