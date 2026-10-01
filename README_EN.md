| [中文](README.md) | [**English**](README_EN.md) |
| :---: | :---: |

<p align="center">
 <h2 align="center">RtCli</h2>
 <p align="center">An AI-powered automatic maintenance, monitoring, and management tool for Minecraft servers</p>
</p>

<div align="center">

![Test Passed](https://img.shields.io/badge/Test-Passed-brightgreen)
![GitHub Repo Size](https://img.shields.io/github/repo-size/psoloi/RutCitrus)
![GitHub License](https://img.shields.io/github/license/psoloi/RutCitrus)
![GitHub Issues](https://img.shields.io/github/issues/psoloi/RutCitrus)
![RCB Status](https://img.shields.io/github/actions/workflow/status/psoloi/RutCitrus/dotnet-console.yml?label=RtCli%20Build)

</div>

## ✨ Usage
This project assumes that you have a basic understanding of Minecraft server administration, and ideally you should already have a server set up.
If you really can't manage, you can use the `.guide` command to quickly create one, then refine the configuration file and use the `.ai` feature.
Obviously, you'll need a good AI model, since an unreliable model may cause unexpected issues.
You should also know how to properly edit configuration files; if you find a bug, please report it on GitHub.

## 🧪 Test Reference
These tests are for reference only, since most features are highly customizable; even if something isn't supported, you can still adapt the configuration file.
However, modded server support is still being improved, and Velocity is recommended for proxy groups.
The following MC server cores have been tested:
 - [x] Paper
   > 1.8.8、1.12.2、1.16.5、1.20.1、1.21.11、26.2
 - [x] Vanilla (Original)
   > 1.8.8、1.12.2、1.16.5、1.18.2、1.19.2、1.20.1、1.20.4、1.20.6、1.21、1.21.11、26.1、26.2、26.3
 - [ ] Folia
   > Pending
 - [ ] Leaf
   > Pending
 - [ ] Velocity (Proxy)
   > Pending

## 🔨 Current Features

 - Unattended automated server maintenance and monitoring, AI memory, AI skills and rules, etc.
 - Full-stack logs let you view most panel events, including AI tool calls
 - MC server creation and import, server JAR download, and server group instance management
 - MC server file management, upload/delete, and plugin updates
 - MC server configuration, including gamerule editing on newer versions and live configuration changes while the server is running
 - Player management such as bans, and player event queries
 - MC console error analysis (basic analysis and AI analysis | AI analysis requires you to prepare your own API and key)
 - Customizable regex matching and scripts for console error analysis (such as the built-in mclog script)
 - Customizable most regex-based configuration content and options
 - Support for custom CSharp and Python scripts, and full extensions (DLL)
 - Customizable task scheduling (customizable trigger methods or conditions, running built-in modules or scripts, etc.)
 - Automatic server backups, filtering of problematic plugins (command line), and filtering of file modifications (command line)
 - Support for controlling and switching between multiple server instances (with feature isolation between servers)
 - Support for splitting the frontend and backend across a LAN (e.g., RtCli on a cloud server and the panel locally)
 - Most features are highly customizable, though some are not yet supported; the code comments are quite easy to understand, and you can try adding features yourself
 - [Rt extension] Support for external Antibot, data packet send rate limiting, and server MOTD status events

## 📘 Project Structure
> [!WARNING]\
> Developers who modify this program must be careful NOT to upload their own AI API keys; please double-check.

This project uses RtCli as the manager for Minecraft servers, and RtPanel as the panel that assists in controlling RtCli; optionally, you can choose Rt as an extension or make your own extension.
RtPanel mainly assists in controlling RtCli and is not recommended to be exposed to the public internet as a standalone panel; as long as you don't share the port, it is generally fine to use.
Set a server port for RtCli, then open RtPanel and connect. If you find the panel unusable or unattractive, any author is welcome to create their own, but the original panel is still recommended.
Rt mainly provides security-related extensions and is still under testing; if you need its features, you can use `ct` to unload the extension.

## 🔌 RtCli Extensions
These are only basic definitions; there are also events used as extension callbacks. See Events.cs in the source code for details.
If you still can't understand, you can try looking at the Rt extension example.
```csharp
    /// <summary>
    /// Plugin interface definition
    /// </summary>
    public interface IExtension
    {
        /// <summary>
        /// Plugin name
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Plugin version
        /// </summary>
        string Version { get; }

        /// <summary>
        /// Plugin description
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Load the plugin
        /// </summary>
        void Load();

        /// <summary>
        /// Run the plugin
        /// </summary>
        void Run();

        /// <summary>
        /// Unload the plugin
        /// </summary>
        void Unload();
    }

    /// <summary>
    /// Plugin information
    /// </summary>
    public class ExtensionInfo
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public string? AssemblyPath { get; set; }
        public string? TypeName { get; set; }
        public bool IsLoaded { get; set; }
        public DateTime LoadTime { get; set; }
    }
```
> [!WARNING]\
> This program is currently not recommended for use on public networks or in dangerous network environments!

## Upcoming Updates
1. Improve the project (yes, you read that right - progress is about 89%)

#### Closing and Credits
When using or modifying this project in any way, we sincerely hope you will keep the original author's name and the original project URL. Thank you for respecting the author! [GitHub](https://github.com/psoloi/RutCitrus)
We highly welcome your feedback. I'm only a high school student and don't have much time, so about half of this program was made with AI assistance. My device may have issues running container programs, and compatibility has not been tested. If you find any errors, I would be very grateful if you could point them out or even improve them. If you have time, could you please give us a Star?
![GitHub Stars](https://img.shields.io/github/stars/psoloi/RutCitrus?logo=github)

This project references many libraries. I deeply respect the efforts of those authors and have not modified their library code. For details, run the `rt about` command in the project to view the actively referenced libraries.
