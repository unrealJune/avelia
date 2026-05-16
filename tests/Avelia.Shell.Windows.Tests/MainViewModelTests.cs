using Avelia.Shell.Windows.ViewModels;
using Xunit;

namespace Avelia.Shell.Windows.Tests;

public class MainViewModelTests
{
    [Fact]
    public void Title_DefaultsToAvelia()
    {
        var vm = new MainViewModel();

        Assert.Equal("Avelia", vm.Title);
    }

    [Fact]
    public void Greeting_DefaultsToWelcomeMessage()
    {
        var vm = new MainViewModel();

        Assert.Equal("Welcome to Avelia.", vm.Greeting);
    }

    [Fact]
    public void GreetCommand_UpdatesGreeting()
    {
        var vm = new MainViewModel();

        vm.GreetCommand.Execute(null);

        Assert.Equal("Hello from Avelia.", vm.Greeting);
    }
}
