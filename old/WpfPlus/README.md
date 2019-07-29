# WPF-Plus Toolkit
_Modern flat themes and useful controls for your next WPF Application_

**License:** MIT

__Features:__
 - flat dark and light styles for the most popular controls
 - highly customizable
 - custom Grid and StackPanel with adjustable column and row spacing
 - SimpleForm container to design forms easier than ever before
 - helper classes for easier MVVM implementation in conjunction with MVVM Light

[Download NuGet-Package](https://www.nuget.org/packages/WpfPlus/)

![Screenshot Dark Theme](http://marcusw.de/screenshots/fddc68f0a9efd497617dcd99444e3d80.png)
![Screenshot Light Theme](http://marcusw.de/screenshots/906218b32cca4002dde62e32cef53340.png)

## Install
Getting started is as simple as adding the `WpfPlus` NuGet-Package to your WPF project. You can do this in the NuGet-Package-Manager or manually:
```
PM> Install-Package WpfPlus 
```

After that you only need to slightly edit your project's `App.xaml` to make it looking like this:
``` xaml
<Application x:Class="MyApplication.App"
             [...]
             xmlns:wpfPlus="clr-namespace:WpfPlus;assembly=WpfPlus"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <wpfPlus:DarkTheme />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```
In case you like the light theme more you can also write `<wpfPlus:LightTheme />` instead.

Finally you can add `Style="{DynamicResource FlatWindowStyle}"` to any window that should apply the flat style.

__That's it. Have fun!__
