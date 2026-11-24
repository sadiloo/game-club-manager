using System;

namespace GameClub.Shell;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => App.RunWithArgs(args);
}