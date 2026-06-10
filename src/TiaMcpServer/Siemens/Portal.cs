using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Compare;
using Siemens.Engineering.Download;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.Online;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.SW.WatchAndForceTables;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcpServer.Siemens
{
    public class Portal
    {
        // closing parantheses for regex characters ommitted, because they are not relevant for regex detection
        private readonly char[] _regexChars = ['.', '^', '$', '*', '+', '?', '(', '[', '{', '\\', '|'];

        private TiaPortal? _portal;
        private ProjectBase? _project;
        private LocalSession? _session;
        private readonly ILogger<Portal>? _logger;

        #region ctor

        public Portal(ILogger<Portal>? logger = null)
        {
            _logger = logger;
        }

        #endregion

        #region helper for mcp server

        public bool ProjectIsValid
        {
            get
            {
                if (_project == null)
                {
                    return false;
                }

                // Check if the project is a valid Project instance
                if ((_session == null) && (_project is Project))
                {
                    return true;
                }

                // If it's a MultiuserProject, we can also check its validity
                if ((_session != null) && (_project is MultiuserProject))
                {
                    return true;
                }

                return false;
            }
        }

        public bool IsLocalSession
        {
            get
            {
                return _session != null;
            }
        }

        public bool IsLocalProject
        {
            get
            {
                return _session == null;
            }
        }

        #endregion

        #region helper for unit tests

        public static bool IsLocalSessionFile(string sessionPath)
        {
            // Check if the path ends with '.als\d+' using regex
            var regex = new Regex(@"\.als\d+$", RegexOptions.IgnoreCase);
            return regex.IsMatch(sessionPath);
        }

        public static bool IsLocalProjectFile(string projectPath)
        {
            // Check if the path ends with '.ap\d+' using regex
            var regex = new Regex(@"\.ap\d+$", RegexOptions.IgnoreCase);
            return regex.IsMatch(projectPath);
        }

        public void Dispose()
        {
            try
            {
                (_project as Project)?.Close();
            }
            catch (Exception)
            {
                // Console.WriteLine($"Error closing the project: {ex.Message}");
            }

            try
            {
                _portal?.Dispose();
            }
            catch (Exception)
            {
                // Console.WriteLine($"Error closing the portal: {ex.Message}");
            }
        }

        #endregion

        #region portal

        public bool ConnectPortal()
        {
            _logger?.LogInformation("Connecting to TIA Portal...");

            try
            {
                _project = null;
                _session = null;
                _portal = null;

                // connect to running TIA Portal
                var processes = TiaPortal.GetProcesses();
                if (processes.Any())
                {
                    _portal = processes.First().Attach();

                    // check for existing local sessions
                    if (_portal.LocalSessions.Any())
                    {
                        _session = _portal.LocalSessions.First();
                        _project = _session.Project;
                    }
                    // checks for existing projects
                    else if (_portal.Projects.Any())
                    {
                        _project = _portal.Projects.First();
                    }

                    return true;
                }

                // start new TIA Portal
                _portal = new TiaPortal(TiaPortalMode.WithUserInterface);

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ConnectPortal failed: {Message}", ex.Message);
                throw new PortalException(PortalErrorCode.InvalidState, $"Connect failed: {ex.GetType().Name}: {ex.Message}", null, ex);
            }
        }

        public bool IsConnected()
        {
            return _portal != null;
        }

        public bool DisconnectPortal()
        {
            _logger?.LogInformation("Disconnecting from TIA Portal...");

            try
            {
                _project = null;
                _session = null;

                _portal?.Dispose();
                _portal = null;

                return true;
            }
            catch (Exception)
            {
                // Handle exception if needed, e.g., log it
            }

            return false;
        }

        #endregion

        #region status

        public State GetState()
        {
            _logger?.LogInformation("Getting TIA Portal state...");
            if (_portal != null)
            {
                // check for existing local sessions
                if (_portal.LocalSessions.Any())
                {
                    _session = _portal.LocalSessions.First();
                    _project = _session.Project;
                }
                // checks for existing projects
                else if (_portal.Projects.Any())
                {
                    _project = _portal.Projects.First();
                }
            }

            return new State
            {
                IsConnected = IsConnected(),
                Project = _project != null ? _project.Name : "-",
                Session = _session != null ? _session.Project.Name : "-"
            };
        }

        #endregion

        #region project

        public List<ProjectBase> GetProjects()
        {
            _logger?.LogInformation("Getting open projects...");

            if (_portal == null)
            {
                _logger?.LogWarning("No TIA Portal instance available.");

                return [];
            }

            var projects = new List<ProjectBase>();

            if (_portal.Projects != null)
            {
                foreach (var project in _portal.Projects)
                {
                    projects.Add(project);
                }
            }

            return projects;
        }

        public bool OpenProject(string projectPath)
        {
            _logger?.LogInformation($"Opening project: {projectPath}");

            if (IsPortalNull())
            {
                return false;
            }

            if (_project != null)
            {
                (_project as Project)?.Close();
                _project = null;
            }

            if (_session != null)
            {
                _session.Close();
                _session = null;
            }

            try
            {
                var projects = GetProjects();
                var projectName = Path.GetFileNameWithoutExtension(projectPath);

                if (!string.IsNullOrEmpty(projectName) && projects.Any(p => p.Name.Equals(projectName)))
                {
                    // Project is already open
                    _project = _portal?.Projects.FirstOrDefault(p => p.Name == projectName);

                    return _project != null;
                }
                else
                {
                    // see [5.3.1 Projekt öffnen, S.113]
                    _project = _portal?.Projects.OpenWithUpgrade(new FileInfo(projectPath));

                    return _project != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public object? GetProjectInfo()
        {
            _logger?.LogInformation("Getting project info...");

            if (IsPortalNull())
            {
                return null;
            }

            if (IsProjectNull())
            {
                return null;
            }

            var project = _project!;

            var info = new
            {
                Name = project.Name,
                Path = project.Path,
                Type = project.GetType().Name,
                IsMultiuserProject = project is MultiuserProject,
                IsLocalSession = _session != null,
                IsLocalProject = _session == null
            };

            return info;
        }

        public bool SaveProject()
        {
            _logger?.LogInformation("Saving project...");

            if (IsProjectNull())
            {
                return false;
            }

            (_project as Project)?.Save();

            return true;
        }

        public string SaveAsProject(string path)
        {
            _logger?.LogInformation($"Saving project as: {path}");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var project = _project as Project;
            if (project == null)
            {
                throw new PortalException(PortalErrorCode.InvalidState, "SaveAs requires a local project (not a multiuser session)");
            }

            // SaveAs needs a new/empty project FOLDER. If the caller passed an existing,
            // non-empty directory (a parent like '...\SaudiArabia'), derive the project
            // folder from the current project name inside it.
            var targetPath = path;
            try
            {
                if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                {
                    targetPath = Path.Combine(path, project.Name);
                }
            }
            catch (Exception)
            {
                // fall through with the caller's path; SaveAs reports the real problem
            }

            try
            {
                project.SaveAs(new DirectoryInfo(targetPath));
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"SaveAs to '{targetPath}' failed: {ex.Message}. " +
                    "Provide a NEW or EMPTY target project folder (e.g. '<parent>\\<NewProjectName>'); " +
                    "a parent directory is also accepted and the current project name is appended.", null, ex);
            }

            // the open project handle changes after SaveAs - re-acquire it
            try
            {
                _session = _portal?.LocalSessions.FirstOrDefault();
                _project = _session != null ? _session.Project : _portal?.Projects.FirstOrDefault();
            }
            catch (Exception)
            {
                TryReattach();
            }

            return targetPath;
        }

        public bool CloseProject()
        {
            _logger?.LogInformation("Closing project...");

            if (IsProjectNull())
            {
                return false;
            }

            (_project as Project)?.Close();
            _project = null;

            return true;
        }

        #endregion

        #region session

        public List<ProjectBase> GetSessions()
        {
            _logger?.LogInformation("Getting open local sessions...");

            if (IsPortalNull())
            {
                return [];
            }

            var sessions = new List<ProjectBase>();

            if (_portal?.LocalSessions != null)
            {
                foreach (var session in _portal.LocalSessions)
                {
                    sessions.Add(session.Project as ProjectBase);
                }
            }

            return sessions;
        }

        public bool OpenSession(string localSessionPath)
        {
            _logger?.LogInformation($"Opening session: {localSessionPath}");

            if (IsPortalNull())
            {
                return false;
            }

            if (_session != null)
            {
                _project = null;
                _session?.Close();
                _session = null;
            }

            try
            {
                var sessions = GetSessions();
                var projectName = Path.GetFileNameWithoutExtension(localSessionPath);
                var sessionName = Regex.Replace(projectName, @"_(LS|ES)_\d$", string.Empty, RegexOptions.IgnoreCase);

                if (!string.IsNullOrEmpty(sessionName) && sessions.Any(s => s.Name.Equals(sessionName)))
                {
                    // Session is already open  
                    _session = _portal?.LocalSessions.FirstOrDefault(s => s.Project.Name == sessionName);
                    if (_session != null)
                    {
                        // Correctly cast MultiuserProject to Project  
                        _project = _session.Project;
                        return _project != null;
                    }
                }
                else
                {
                    _session = _portal?.LocalSessions.Open(new FileInfo(localSessionPath));
                    if (_session != null)
                    {
                        // Correctly cast MultiuserProject to Project  
                        _project = _session.Project;
                        return _project != null;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }

        public bool SaveSession()
        {
            _logger?.LogInformation("Saving session...");

            if (IsSessionNull())
            {
                return false;
            }

            // Save session
            _session?.Save();

            return true;
        }

        public bool CloseSession()
        {
            _logger?.LogInformation("Closing session...");

            if (IsSessionNull())
            {
                return false;
            }

            _project = null;
            _session?.Close();
            _session = null;

            return true;
        }

        #endregion

        #region devices

        public string GetProjectTree()
        {
            _logger?.LogInformation("Getting project tree...");

            if (IsProjectNull())
            {
                return string.Empty;
            }

            StringBuilder sb = new();

            sb.AppendLine($"{_project?.Name}");

            var ancestorStates = new List<bool>();
            var sections = new List<Action>();
            
            if (_project?.Devices != null && _project.Devices.Count > 0)
            {
                sections.Add(() => GetProjectTreeDevices(sb, _project.Devices, ancestorStates));
            }
            
            if (_project?.DeviceGroups != null && _project.DeviceGroups.Count > 0)
            {
                sections.Add(() => GetProjectTreeGroups(sb, _project.DeviceGroups, ancestorStates));
            }
            
            if (_project?.UngroupedDevicesGroup != null)
            {
                sections.Add(() => GetProjectTreeUngroupedDeviceGroup(sb, _project.UngroupedDevicesGroup, ancestorStates));
            }
            
            for (int i = 0; i < sections.Count; i++)
            {
                var isLastSection = i == sections.Count - 1;
                if (i == 0)
                {
                    sections[i]();
                }
                else
                {
                    sections[i]();
                }
            }

            return sb.ToString();
        }

        

        public List<Device> GetDevices(string regexName = "")
        {
            _logger?.LogInformation("Getting devices...");

            if (IsProjectNull())
            {
                return [];
            }

            var list = new List<Device>();

            if (_project?.Devices != null)
            {
                foreach (Device device in _project.Devices)
                {
                    list.Add(device);
                }

                foreach (var group in _project.DeviceGroups)
                {
                    GetDevicesRecursive(group, list, regexName);
                }

                // Ungrouped devices live in a DeviceSystemGroup (flat, no user subgroups),
                // so iterate its Devices directly rather than via GetDevicesRecursive.
                if (_project?.UngroupedDevicesGroup?.Devices != null)
                {
                    foreach (Device device in _project.UngroupedDevicesGroup.Devices)
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(device.Name, regexName, RegexOptions.IgnoreCase))
                            {
                                continue; // Skip this device if it doesn't match the pattern
                            }
                        }
                        catch (Exception)
                        {
                            // Invalid regex pattern, skip this device
                            continue;
                        }

                        list.Add(device);
                    }
                }
            }

            return list;
        }

        public Device? GetDevice(string devicePath)
        {
            _logger?.LogInformation($"Getting device by path: {devicePath}");

            if (IsProjectNull())
            {
                return null;
            }

            // Retrieve the device by its path
            return GetDeviceByPath(devicePath);
        }

        public DeviceItem? GetDeviceItem(string deviceItemPath)
        {
            _logger?.LogInformation($"Getting device item by path: {deviceItemPath}");

            if (IsProjectNull())
            {
                return null;
            }

            // Retrieve the device by its path
            return GetDeviceItemByPath(deviceItemPath);

        }

        public Device CreateDevice(string typeIdentifier, string name, string deviceName)
        {
            _logger?.LogInformation($"Creating device: {name} ({typeIdentifier})...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            try
            {
                var device = (_project as Project)?.Devices.CreateWithItem(typeIdentifier, name, deviceName);

                if (device == null)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Failed to create device '{deviceName}' with type '{typeIdentifier}'");
                }

                return device;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to create device '{deviceName}'", null, ex);
                pex.Data["typeIdentifier"] = typeIdentifier;
                pex.Data["name"] = name;
                pex.Data["deviceName"] = deviceName;
                _logger?.LogError(pex, "CreateDevice failed for {DeviceName} ({TypeIdentifier})", deviceName, typeIdentifier);
                throw pex;
            }
        }

        public void DeleteDevice(string devicePath)
        {
            _logger?.LogInformation($"Deleting device: {devicePath}...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            try
            {
                var device = GetDeviceByPath(devicePath);

                if (device == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Device not found at path '{devicePath}'");
                }

                device.Delete();
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to delete device at '{devicePath}'", null, ex);
                pex.Data["devicePath"] = devicePath;
                _logger?.LogError(pex, "DeleteDevice failed for {DevicePath}", devicePath);
                throw pex;
            }
        }

        public DeviceUserGroup CreateDeviceGroup(string groupName, string parentGroupPath = "")
        {
            _logger?.LogInformation($"Creating device group: {groupName}...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            try
            {
                DeviceUserGroupComposition? groups = (_project as Project)?.DeviceGroups;

                if (groups == null)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Cannot access device groups");
                }

                if (!string.IsNullOrEmpty(parentGroupPath))
                {
                    var pathSegments = parentGroupPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    DeviceUserGroup? currentGroup = null;

                    foreach (var segment in pathSegments)
                    {
                        currentGroup = (currentGroup?.Groups ?? groups)
                            .FirstOrDefault(g => g.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));

                        if (currentGroup == null)
                        {
                            throw new PortalException(PortalErrorCode.NotFound, $"Parent group path '{parentGroupPath}' not found");
                        }
                    }

                    return currentGroup!.Groups.Create(groupName);
                }

                return groups.Create(groupName);
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to create device group '{groupName}'", null, ex);
                pex.Data["groupName"] = groupName;
                pex.Data["parentGroupPath"] = parentGroupPath;
                _logger?.LogError(pex, "CreateDeviceGroup failed for {GroupName}", groupName);
                throw pex;
            }
        }

        public List<DeviceItem> GetModules(string deviceItemPath)
        {
            _logger?.LogInformation($"Getting modules for device item: {deviceItemPath}...");

            if (IsProjectNull())
            {
                return [];
            }

            try
            {
                var deviceItem = GetDeviceItemByPath(deviceItemPath);

                if (deviceItem == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Device item not found at path '{deviceItemPath}'");
                }

                var modules = new List<DeviceItem>();

                foreach (DeviceItem subItem in deviceItem.DeviceItems)
                {
                    modules.Add(subItem);
                }

                return modules;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to get modules for '{deviceItemPath}'", null, ex);
                pex.Data["deviceItemPath"] = deviceItemPath;
                _logger?.LogError(pex, "GetModules failed for {DeviceItemPath}", deviceItemPath);
                throw pex;
            }
        }

        public DeviceItem GetModuleInfo(string deviceItemPath)
        {
            _logger?.LogInformation($"Getting module info for: {deviceItemPath}...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            try
            {
                var deviceItem = GetDeviceItemByPath(deviceItemPath);

                if (deviceItem == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Device item not found at path '{deviceItemPath}'");
                }

                return deviceItem;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to get module info for '{deviceItemPath}'", null, ex);
                pex.Data["deviceItemPath"] = deviceItemPath;
                _logger?.LogError(pex, "GetModuleInfo failed for {DeviceItemPath}", deviceItemPath);
                throw pex;
            }
        }

        public List<(int StartAddress, int Length, string IoType)> GetAddresses(string deviceItemPath)
        {
            _logger?.LogInformation($"Getting addresses for: {deviceItemPath}...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            try
            {
                var deviceItem = GetDeviceItemByPath(deviceItemPath);

                if (deviceItem == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Device item not found at path '{deviceItemPath}'");
                }

                var addresses = new List<(int StartAddress, int Length, string IoType)>();

                foreach (var address in deviceItem.Addresses)
                {
                    var startAddress = address.StartAddress;
                    var length = address.Length;
                    var ioType = address.IoType.ToString();
                    addresses.Add((startAddress, length, ioType));
                }

                return addresses;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to get addresses for '{deviceItemPath}'", null, ex);
                pex.Data["deviceItemPath"] = deviceItemPath;
                _logger?.LogError(pex, "GetAddresses failed for {DeviceItemPath}", deviceItemPath);
                throw pex;
            }
        }

        /// <summary>
        /// Plugs a new module into a device or device item slot - ET200SP cards by
        /// order number (e.g. 'OrderNumber:6ES7 131-6BH01-0BA0/V1.1') or GSD
        /// sub-modules by their GSD type identifier. MUTATES the project.
        /// </summary>
        public DeviceItem PlugModule(string parentPath, string typeIdentifier, string name, int positionNumber)
        {
            _logger?.LogInformation($"Plugging module '{typeIdentifier}' as '{name}' at position {positionNumber} into '{parentPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                // the parent may be a device (rack container) or a device item (rack, head module, IO-Link master)
                HardwareObject? parent = GetDeviceItemByPath(parentPath);
                if (parent == null)
                {
                    parent = GetDeviceByPath(parentPath);
                }

                if (parent == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No device or device item found at path '{parentPath}'. Use GetDeviceTree to list the exact paths.");
                }

                if (!parent.CanPlugNew(typeIdentifier, name, positionNumber))
                {
                    throw new PortalException(PortalErrorCode.InvalidParams,
                        $"Cannot plug '{typeIdentifier}' at position {positionNumber} of '{parentPath}'. " +
                        "Check the type identifier (exact order number + version), that the position is free, " +
                        "and that the parent is the correct rack/head module level (use GetDeviceTree).");
                }

                var item = RunWithTimeout(() => parent.PlugNew(typeIdentifier, name, positionNumber), 60, "PlugModule");

                if (item == null)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, $"PlugNew returned no device item for '{typeIdentifier}'");
                }

                return item;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to plug module '{typeIdentifier}' into '{parentPath}'", null, ex);
                pex.Data["parentPath"] = parentPath;
                pex.Data["typeIdentifier"] = typeIdentifier;
                pex.Data["name"] = name;
                pex.Data["positionNumber"] = positionNumber;
                _logger?.LogError(pex, "PlugModule failed for {ParentPath} {TypeIdentifier}", parentPath, typeIdentifier);
                throw pex;
            }
        }

        /// <summary>
        /// Unplugs (deletes) a module device item. MUTATES the project.
        /// </summary>
        public void UnplugModule(string deviceItemPath)
        {
            _logger?.LogInformation($"Unplugging module at '{deviceItemPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var item = GetDeviceItemByPath(deviceItemPath);
                if (item == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Device item not found at path '{deviceItemPath}'. Use GetDeviceTree to list the exact paths.");
                }

                RunWithTimeout<object?>(() => { item.Delete(); return null; }, 60, "UnplugModule");
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to unplug module at '{deviceItemPath}'", null, ex);
                pex.Data["deviceItemPath"] = deviceItemPath;
                _logger?.LogError(pex, "UnplugModule failed for {DeviceItemPath}", deviceItemPath);
                throw pex;
            }
        }

        /// <summary>
        /// Sets a module's I/O start address (byte), e.g. to pin an IO-Link encoder
        /// at %ID100 instead of accepting the auto-assigned address. MUTATES the project.
        /// </summary>
        public (int OldStart, int NewStart, int Length, string IoType) SetModuleAddress(string deviceItemPath, string ioType, int startAddress)
        {
            _logger?.LogInformation($"Setting {ioType} start address of '{deviceItemPath}' to {startAddress}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var item = GetDeviceItemByPath(deviceItemPath);
                if (item == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Device item not found at path '{deviceItemPath}'. Use GetDeviceTree to list the exact paths.");
                }

                Address? address = null;
                var available = new List<string>();
                foreach (var a in item.Addresses)
                {
                    available.Add($"{a.IoType} (start {a.StartAddress}, length {a.Length})");
                    if (a.IoType.ToString().Equals(ioType, StringComparison.OrdinalIgnoreCase))
                    {
                        address = a;
                    }
                }

                if (address == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound,
                        $"No '{ioType}' address on '{deviceItemPath}'.", available);
                }

                var oldStart = address.StartAddress;
                var target = address;
                RunWithTimeout<object?>(() => { target.SetAttribute("StartAddress", startAddress); return null; }, 30, "SetModuleAddress");

                return (oldStart, target.StartAddress, target.Length, target.IoType.ToString());
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams,
                    $"Failed to set {ioType} start address of '{deviceItemPath}' to {startAddress}: {DescribeException(ex)}. " +
                    "The address may overlap another module or exceed the CPU's process image - check the address space.", null, ex);
                pex.Data["deviceItemPath"] = deviceItemPath;
                pex.Data["ioType"] = ioType;
                pex.Data["startAddress"] = startAddress;
                _logger?.LogError(pex, "SetModuleAddress failed for {DeviceItemPath}", deviceItemPath);
                throw pex;
            }
        }

        // Resolves a path to the NetworkInterface feature it carries. Accepts the
        // interface device item itself, or any device/rack/head path - in that case
        // the first NetworkInterface-bearing descendant is used (the realistic input,
        // since users pass 'ET200SP' rather than '.../PROFINET interface_1').
        private NetworkInterface ResolveNetworkInterface(string path, out string resolvedName)
        {
            var deviceItem = GetDeviceItemByPath(path);
            if (deviceItem != null)
            {
                var itf = FindNetworkInterface(deviceItem);
                if (itf != null)
                {
                    resolvedName = deviceItem.Name;
                    return itf;
                }
            }

            var device = GetDeviceByPath(path);
            if (device != null)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    var itf = FindNetworkInterface(item);
                    if (itf != null)
                    {
                        resolvedName = device.Name;
                        return itf;
                    }
                }
            }

            throw new PortalException(PortalErrorCode.NotFound,
                $"No network interface found at or below '{path}'. Use GetDeviceTree to find the PROFINET interface item.");
        }

        /// <summary>
        /// Creates a PROFINET/Ethernet subnet seeded from a device interface's node,
        /// or connects the node to the subnet if it already exists (idempotent).
        /// MUTATES the project.
        /// </summary>
        public (string SubnetName, string ConnectedInterface, bool Created) CreateSubnet(string deviceItemPath, string subnetName)
        {
            _logger?.LogInformation($"Creating subnet '{subnetName}' from '{deviceItemPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                if (string.IsNullOrWhiteSpace(subnetName))
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "subnetName cannot be empty");
                }

                var itf = ResolveNetworkInterface(deviceItemPath, out var resolvedName);
                if (itf.Nodes == null || itf.Nodes.Count == 0)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, $"The network interface at '{deviceItemPath}' has no nodes");
                }

                var node = itf.Nodes[0];
                var project = _project as Project;
                var existing = project?.Subnets?.FirstOrDefault(s => s.Name.Equals(subnetName, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    var connected = node.ConnectedSubnet;
                    if (connected == null || !connected.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        RunWithTimeout<object?>(() => { node.ConnectToSubnet(existing); return null; }, 60, "CreateSubnet(connect)");
                    }
                    return (existing.Name, resolvedName, false);
                }

                var subnet = RunWithTimeout(() => node.CreateAndConnectToSubnet(subnetName), 60, "CreateSubnet");
                return (subnet.Name, resolvedName, true);
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to create subnet '{subnetName}' from '{deviceItemPath}'", null, ex);
                pex.Data["deviceItemPath"] = deviceItemPath;
                pex.Data["subnetName"] = subnetName;
                _logger?.LogError(pex, "CreateSubnet failed for {DeviceItemPath} {SubnetName}", deviceItemPath, subnetName);
                throw pex;
            }
        }

        /// <summary>
        /// Networks a PROFINET IO device to a controller: puts both interfaces on the
        /// subnet (created if needed), ensures the controller has an IO system, and
        /// connects the device's IO connector to it - the step that assigns real
        /// %I/%Q addresses to plugged modules. Optionally sets IP addresses.
        /// Idempotent for the subnet, IO system and an existing connection.
        /// MUTATES the project.
        /// </summary>
        public (string Device, string Controller, string SubnetName, string IoSystemName, List<(string Module, string IoType, int StartAddress, int Length)> Addresses)
            ConnectIoDevice(string deviceItemPath, string controllerItemPath, string subnetName = "PN/IE_1", string ioSystemName = "PROFINET IO-System", string deviceIp = "", string controllerIp = "")
        {
            _logger?.LogInformation($"Connecting IO device '{deviceItemPath}' to controller '{controllerItemPath}' (subnet '{subnetName}', IO system '{ioSystemName}')");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var devItf = ResolveNetworkInterface(deviceItemPath, out var deviceName);
                var ctrlItf = ResolveNetworkInterface(controllerItemPath, out var controllerName);

                if (ctrlItf.IoControllers == null || ctrlItf.IoControllers.Count == 0)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams,
                        $"'{controllerItemPath}' is not a PROFINET IO controller (no IoControllers on its interface). Pass the CPU's PROFINET interface.");
                }
                if (devItf.IoConnectors == null || devItf.IoConnectors.Count == 0)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams,
                        $"'{deviceItemPath}' is not a PROFINET IO device (no IoConnectors on its interface). Pass the IO device's PROFINET interface.");
                }

                // 1. subnet: create from the controller side, then bring the device node onto it
                var (actualSubnetName, _, _) = CreateSubnet(controllerItemPath, subnetName);
                var project = _project as Project;
                var subnet = project!.Subnets.First(s => s.Name.Equals(actualSubnetName, StringComparison.OrdinalIgnoreCase));

                var devNode = devItf.Nodes[0];
                var devConnected = devNode.ConnectedSubnet;
                if (devConnected == null || !devConnected.Name.Equals(subnet.Name, StringComparison.OrdinalIgnoreCase))
                {
                    RunWithTimeout<object?>(() => { devNode.ConnectToSubnet(subnet); return null; }, 60, "ConnectIoDevice(subnet)");
                }

                // 2. IO system on the controller
                var ioController = ctrlItf.IoControllers[0];
                var ioSystem = ioController.IoSystem;
                if (ioSystem == null)
                {
                    ioSystem = RunWithTimeout(() => ioController.CreateIoSystem(ioSystemName), 60, "ConnectIoDevice(CreateIoSystem)");
                }

                // 3. assign the device to the IO system
                var connector = devItf.IoConnectors[0];
                var alreadyConnected = false;
                try
                {
                    var current = connector.ConnectedToIoSystem;
                    alreadyConnected = current != null && current.Name.Equals(ioSystem.Name, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                }
                if (!alreadyConnected)
                {
                    var system = ioSystem;
                    RunWithTimeout<object?>(() => { connector.ConnectToIoSystem(system); return null; }, 60, "ConnectIoDevice(ConnectToIoSystem)");
                }

                // 4. optional IP addresses
                if (!string.IsNullOrWhiteSpace(controllerIp))
                {
                    try { ctrlItf.Nodes[0].SetAttribute("Address", controllerIp); }
                    catch (Exception ex) { _logger?.LogWarning(ex, "Setting controller IP failed"); }
                }
                if (!string.IsNullOrWhiteSpace(deviceIp))
                {
                    try { devNode.SetAttribute("Address", deviceIp); }
                    catch (Exception ex) { _logger?.LogWarning(ex, "Setting device IP failed"); }
                }

                // 5. read back the device's now-assigned addresses for confirmation
                var addresses = new List<(string Module, string IoType, int StartAddress, int Length)>();
                var devItem = GetDeviceItemByPath(deviceItemPath);
                var root = devItem != null ? FindRootDevice(devItem) : GetDeviceByPath(deviceItemPath);
                if (root != null)
                {
                    CollectAssignedAddresses(root.DeviceItems, addresses);
                }

                return (deviceName, controllerName, subnet.Name, ioSystem.Name, addresses);
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to connect IO device '{deviceItemPath}' to controller '{controllerItemPath}'", null, ex);
                pex.Data["deviceItemPath"] = deviceItemPath;
                pex.Data["controllerItemPath"] = controllerItemPath;
                pex.Data["subnetName"] = subnetName;
                pex.Data["ioSystemName"] = ioSystemName;
                _logger?.LogError(pex, "ConnectIoDevice failed for {DeviceItemPath} -> {ControllerItemPath}", deviceItemPath, controllerItemPath);
                throw pex;
            }
        }

        private static Device? FindRootDevice(DeviceItem item)
        {
            object? current = item;
            while (current is DeviceItem di)
            {
                current = di.Parent;
            }
            return current as Device;
        }

        private static void CollectAssignedAddresses(DeviceItemComposition items, List<(string Module, string IoType, int StartAddress, int Length)> list)
        {
            foreach (DeviceItem item in items)
            {
                try
                {
                    foreach (var address in item.Addresses)
                    {
                        if (address.StartAddress >= 0)
                        {
                            list.Add((item.Name, address.IoType.ToString(), address.StartAddress, address.Length));
                        }
                    }
                }
                catch (Exception)
                {
                }

                CollectAssignedAddresses(item.DeviceItems, list);
            }
        }

        /// <summary>
        /// HMI and GSD devices often carry an empty TypeIdentifier at the queried level -
        /// fall back to the first non-empty TypeIdentifier / OrderNumber / TypeName found
        /// on the object itself or (breadth-first) its device items, so the hardware
        /// model is discoverable from the project.
        /// </summary>
        public string ResolveTypeIdentifier(HardwareObject hardwareObject)
        {
            static string Probe(IEngineeringObject obj)
            {
                foreach (var attr in new[] { "TypeIdentifier", "OrderNumber", "TypeName" })
                {
                    try
                    {
                        var value = obj.GetAttribute(attr)?.ToString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            return value;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                return "";
            }

            var own = Probe(hardwareObject);
            if (!string.IsNullOrEmpty(own))
            {
                return own;
            }

            var queue = new Queue<DeviceItem>();
            var items = (hardwareObject as Device)?.DeviceItems ?? (hardwareObject as DeviceItem)?.DeviceItems;
            if (items != null)
            {
                foreach (DeviceItem item in items)
                {
                    queue.Enqueue(item);
                }
            }

            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                var value = Probe(item);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
                foreach (DeviceItem sub in item.DeviceItems)
                {
                    queue.Enqueue(sub);
                }
            }

            return "";
        }

        /// <summary>
        /// Renders the device-item hierarchy with the exact path segments the other
        /// device tools resolve, so GSD/ungrouped device internals are discoverable.
        /// </summary>
        public string GetDeviceTree(string devicePath = "")
        {
            _logger?.LogInformation($"Getting device tree for: '{devicePath}'...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(devicePath))
            {
                var device = GetDeviceByPath(devicePath);
                if (device != null)
                {
                    AppendDeviceTree(sb, device);
                    return sb.ToString();
                }

                var deviceItem = GetDeviceItemByPath(devicePath);
                if (deviceItem == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No device or device item found at path '{devicePath}'");
                }

                AppendDeviceItemTree(sb, deviceItem, 0);
                return sb.ToString();
            }

            if (_project?.Devices != null)
            {
                foreach (Device device in _project.Devices)
                {
                    AppendDeviceTree(sb, device);
                }
            }

            AppendDeviceGroupTrees(sb, _project?.DeviceGroups, "");

            if (_project?.UngroupedDevicesGroup?.Devices != null)
            {
                sb.AppendLine("[Ungrouped devices]");
                foreach (Device device in _project.UngroupedDevicesGroup.Devices)
                {
                    AppendDeviceTree(sb, device);
                }
            }

            return sb.ToString();
        }

        private void AppendDeviceGroupTrees(StringBuilder sb, DeviceUserGroupComposition? groups, string prefix)
        {
            if (groups == null)
            {
                return;
            }

            foreach (DeviceUserGroup group in groups)
            {
                sb.AppendLine($"[Group] {prefix}{group.Name}");
                foreach (Device device in group.Devices)
                {
                    AppendDeviceTree(sb, device, $"{prefix}{group.Name}/");
                }
                AppendDeviceGroupTrees(sb, group.Groups, $"{prefix}{group.Name}/");
            }
        }

        private void AppendDeviceTree(StringBuilder sb, Device device, string prefix = "")
        {
            var typeId = "";
            try { typeId = device.TypeIdentifier ?? ""; } catch { }
            sb.AppendLine($"Device: {prefix}{device.Name}{(string.IsNullOrEmpty(typeId) ? "" : $" [{typeId}]")}");

            foreach (DeviceItem item in device.DeviceItems)
            {
                AppendDeviceItemTree(sb, item, 1);
            }
        }

        private void AppendDeviceItemTree(StringBuilder sb, DeviceItem item, int depth)
        {
            var indent = new string(' ', depth * 2);

            var typeId = "";
            try { typeId = item.TypeIdentifier ?? ""; } catch { }
            if (string.IsNullOrEmpty(typeId))
            {
                try { typeId = item.GetAttribute("OrderNumber")?.ToString() ?? ""; } catch { }
            }

            var position = "";
            try { position = $" pos={item.PositionNumber}"; } catch { }

            var addresses = "";
            try
            {
                var parts = new List<string>();
                foreach (var address in item.Addresses)
                {
                    parts.Add($"%{(address.IoType.ToString() == "Input" ? "I" : address.IoType.ToString() == "Output" ? "Q" : address.IoType.ToString())}{address.StartAddress}..{address.StartAddress + Math.Max(address.Length / 8 - 1, 0)}");
                }
                if (parts.Count > 0)
                {
                    addresses = $" addresses: {string.Join(", ", parts)}";
                }
            }
            catch { }

            sb.AppendLine($"{indent}- {item.Name}{(string.IsNullOrEmpty(typeId) ? "" : $" [{typeId}]")}{position}{addresses}");

            foreach (DeviceItem subItem in item.DeviceItems)
            {
                AppendDeviceItemTree(sb, subItem, depth + 1);
            }
        }

        public List<Subnet> GetSubnets()
        {
            _logger?.LogInformation("Getting subnets...");

            if (IsProjectNull())
            {
                return [];
            }

            try
            {
                var subnets = new List<Subnet>();
                var project = _project as Project;

                if (project?.Subnets != null)
                {
                    foreach (Subnet subnet in project.Subnets)
                    {
                        subnets.Add(subnet);
                    }
                }

                return subnets;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, "Failed to get subnets", null, ex);
                _logger?.LogError(pex, "GetSubnets failed");
                throw pex;
            }
        }

        public List<(string Name, string InterfaceType, string IpAddress, string SubnetMask)> GetNetworkInterfaces(string deviceItemPath)
        {
            _logger?.LogInformation($"Getting network interfaces for: {deviceItemPath}...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            try
            {
                var deviceItem = GetDeviceItemByPath(deviceItemPath);

                if (deviceItem == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Device item not found at path '{deviceItemPath}'");
                }

                var interfaces = new List<(string Name, string InterfaceType, string IpAddress, string SubnetMask)>();

                CollectNetworkInterfaces(deviceItem, interfaces);

                return interfaces;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to get network interfaces for '{deviceItemPath}'", null, ex);
                pex.Data["deviceItemPath"] = deviceItemPath;
                _logger?.LogError(pex, "GetNetworkInterfaces failed for {DeviceItemPath}", deviceItemPath);
                throw pex;
            }
        }

        public void SetIpAddress(string deviceItemPath, string ipAddress, string subnetMask, string routerAddress = "")
        {
            _logger?.LogInformation($"Setting IP address for: {deviceItemPath} to {ipAddress}...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            try
            {
                var deviceItem = GetDeviceItemByPath(deviceItemPath);

                if (deviceItem == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Device item not found at path '{deviceItemPath}'");
                }

                var networkInterface = FindNetworkInterface(deviceItem);

                if (networkInterface == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No network interface found on device item '{deviceItemPath}'");
                }

                var nodes = networkInterface.Nodes;
                foreach (Node node in nodes)
                {
                    node.SetAttribute("Address", ipAddress);
                    node.SetAttribute("SubnetMask", subnetMask);

                    if (!string.IsNullOrEmpty(routerAddress))
                    {
                        node.SetAttribute("RouterAddress", routerAddress);
                    }
                }
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to set IP address for '{deviceItemPath}'", null, ex);
                pex.Data["deviceItemPath"] = deviceItemPath;
                pex.Data["ipAddress"] = ipAddress;
                pex.Data["subnetMask"] = subnetMask;
                _logger?.LogError(pex, "SetIpAddress failed for {DeviceItemPath}", deviceItemPath);
                throw pex;
            }
        }

        public void ConnectToSubnet(string deviceItemPath, string subnetName)
        {
            _logger?.LogInformation($"Connecting {deviceItemPath} to subnet {subnetName}...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            try
            {
                var deviceItem = GetDeviceItemByPath(deviceItemPath);

                if (deviceItem == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Device item not found at path '{deviceItemPath}'");
                }

                var project = _project as Project;
                var subnet = project?.Subnets?.FirstOrDefault(s => s.Name.Equals(subnetName, StringComparison.OrdinalIgnoreCase));

                if (subnet == null)
                {
                    var availableSubnets = project?.Subnets?.Select(s => s.Name) ?? Enumerable.Empty<string>();
                    throw new PortalException(PortalErrorCode.NotFound, $"Subnet '{subnetName}' not found", availableSubnets);
                }

                var networkInterface = FindNetworkInterface(deviceItem);

                if (networkInterface == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No network interface found on device item '{deviceItemPath}'");
                }

                var nodes = networkInterface.Nodes;
                foreach (Node node in nodes)
                {
                    node.ConnectToSubnet(subnet);
                }
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to connect '{deviceItemPath}' to subnet '{subnetName}'", null, ex);
                pex.Data["deviceItemPath"] = deviceItemPath;
                pex.Data["subnetName"] = subnetName;
                _logger?.LogError(pex, "ConnectToSubnet failed for {DeviceItemPath} -> {SubnetName}", deviceItemPath, subnetName);
                throw pex;
            }
        }

        public void ImportGsdFile(string gsdFilePath)
        {
            _logger?.LogInformation($"Importing GSD file: {gsdFilePath}...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            try
            {
                if (!File.Exists(gsdFilePath))
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"GSD file not found at '{gsdFilePath}'");
                }

                var project = _project as Project;

                if (project == null)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Project must be a local project to import GSD files");
                }

                // GSD/GSDML installation is a TIA Portal-wide operation that the
                // Openness API does not expose (it requires a TIA restart and runs
                // outside any project).
                throw new PortalException(PortalErrorCode.InvalidState,
                    "TIA Openness cannot install GSD/GSDML files. Install it in TIA Portal via " +
                    "'Options > Manage general station description files (GSD)', restart TIA Portal, then reconnect.");
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to import GSD file '{gsdFilePath}'", null, ex);
                pex.Data["gsdFilePath"] = gsdFilePath;
                _logger?.LogError(pex, "ImportGsdFile failed for {GsdFilePath}", gsdFilePath);
                throw pex;
            }
        }

        #endregion

        #region hardware and network helpers

        private void CollectNetworkInterfaces(DeviceItem deviceItem, List<(string Name, string InterfaceType, string IpAddress, string SubnetMask)> interfaces)
        {
            var networkInterface = deviceItem.GetService<NetworkInterface>();

            if (networkInterface != null)
            {
                var ipAddress = "";
                var subnetMask = "";
                var interfaceType = networkInterface.InterfaceType.ToString();

                foreach (Node node in networkInterface.Nodes)
                {
                    try
                    {
                        ipAddress = node.GetAttribute("Address")?.ToString() ?? "";
                        subnetMask = node.GetAttribute("SubnetMask")?.ToString() ?? "";
                    }
                    catch
                    {
                        // Some nodes may not have IP attributes
                    }
                }

                interfaces.Add((deviceItem.Name, interfaceType, ipAddress, subnetMask));
            }

            foreach (DeviceItem subItem in deviceItem.DeviceItems)
            {
                CollectNetworkInterfaces(subItem, interfaces);
            }
        }

        private NetworkInterface? FindNetworkInterface(DeviceItem deviceItem)
        {
            var networkInterface = deviceItem.GetService<NetworkInterface>();

            if (networkInterface != null)
            {
                return networkInterface;
            }

            foreach (DeviceItem subItem in deviceItem.DeviceItems)
            {
                networkInterface = FindNetworkInterface(subItem);
                if (networkInterface != null)
                {
                    return networkInterface;
                }
            }

            return null;
        }

        #endregion

        #region software

        public PlcSoftware? GetPlcSoftware(string softwarePath)
        {
            _logger?.LogInformation($"Getting software by path: {softwarePath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);

            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                return plcSoftware;
            }

            return null;
        }

        public CompilerResult? CompileSoftware(string softwarePath, string password = "")
        {
            _logger?.LogInformation($"Compiling software by path: {softwarePath}");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open");
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);

            if (!string.IsNullOrEmpty(password))
            {
                var deviceItem = softwareContainer?.Parent as DeviceItem;

                var admin = deviceItem?.GetService<SafetyAdministration>();
                if (admin != null)
                {
                    if (!admin.IsLoggedOnToSafetyOfflineProgram)
                    {
                        SecureString secString = new NetworkCredential("", password).SecurePassword;
                        try
                        {
                            admin.LoginToSafetyOfflineProgram(secString);
                        }
                        catch (Exception ex)
                        {
                            throw new PortalException(PortalErrorCode.InvalidState, "Failed to login to safety offline program", null, ex);
                        }
                    }
                }
            }

            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                try
                {
                    ICompilable compileService = plcSoftware.GetService<ICompilable>();
                    CompilerResult result = compileService.Compile();
                    _logger?.LogInformation($"Compile result: {result.State}, messages: {result.Messages.Count()}");
                    return result;
                }
                catch (Exception ex)
                {
                    throw new PortalException(PortalErrorCode.ExportFailed, $"Compilation failed: {ex.Message}", null, ex);
                }
            }

            throw new PortalException(PortalErrorCode.NotFound, $"No PLC software found at path: {softwarePath}");
        }

        /// <summary>
        /// Recursively collects all compile messages including sub-messages with full depth
        /// </summary>
        public static void CollectCompileMessages(IEnumerable<CompilerResultMessage> messages, List<string> output, int depth = 0)
        {
            foreach (var msg in messages)
            {
                var indent = new string(' ', depth * 2);
                var state = msg.State.ToString();
                var path = "";
                var description = "";

                try { path = msg.Path ?? ""; } catch { }
                try { description = msg.Description ?? ""; } catch { }

                if (!string.IsNullOrEmpty(description) || !string.IsNullOrEmpty(path))
                {
                    output.Add($"{indent}[{state}] {path}: {description}");
                }

                // Recurse into sub-messages for detailed errors
                try
                {
                    if (msg.Messages != null && msg.Messages.Any())
                    {
                        CollectCompileMessages(msg.Messages, output, depth + 1);
                    }
                }
                catch { }
            }
        }

        #endregion

        #region blocks/types

        public PlcBlock? GetBlock(string softwarePath, string blockPath)
        {
            _logger?.LogInformation($"Getting block by path: {blockPath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var blockGroup = plcSoftware?.BlockGroup;

                if (blockGroup != null)
                {
                    var path = blockPath.Contains("/") ? blockPath.Substring(0, blockPath.LastIndexOf("/")) : string.Empty;
                    var regexName = blockPath.Contains("/") ? blockPath.Substring(blockPath.LastIndexOf("/") + 1) : blockPath;

                    PlcBlock? block = null;

                    var group = GetPlcBlockGroupByPath(softwarePath, path);
                    if (group != null)
                    {
                        if (regexName.IndexOfAny(_regexChars) >= 0)
                        {
                            try
                            {
                                var regex = new Regex(regexName, RegexOptions.IgnoreCase);
                                block = group.Blocks.FirstOrDefault(b => regex.IsMatch(b.Name)) as PlcBlock;
                            }
                            catch (Exception)
                            {
                                // Invalid regex, return null
                                return null;
                            }
                        }
                        else
                        {
                            block = group.Blocks.FirstOrDefault(b => b.Name.Equals(regexName, StringComparison.OrdinalIgnoreCase));
                        }

                        return block;
                    }
                }
            }

            return null;
        }

        public PlcType? GetType(string softwarePath, string typePath)
        {
            _logger?.LogInformation($"Getting type by path: {typePath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var typeGroup = plcSoftware?.TypeGroup;

                if (typeGroup != null)
                {
                    var path = typePath.Contains("/") ? typePath.Substring(0, typePath.LastIndexOf("/")) : string.Empty;
                    var regexName = typePath.Contains("/") ? typePath.Substring(typePath.LastIndexOf("/") + 1) : typePath;

                    PlcType? type = null;

                    var group = GetPlcTypeGroupByPath(softwarePath, path);
                    if (group != null)
                    {
                        if (regexName.IndexOfAny(_regexChars) >= 0)
                        {
                            try
                            {
                                var regex = new Regex(regexName, RegexOptions.IgnoreCase);
                                type = group.Types.FirstOrDefault(t => regex.IsMatch(t.Name)) as PlcType;
                            }
                            catch (Exception)
                            {
                                // Invalid regex, return null
                                return null;
                            }
                        }
                        else
                        {
                            type = group.Types.FirstOrDefault(t => t.Name.Equals(regexName, StringComparison.OrdinalIgnoreCase));
                        }

                        return type;
                    }
                }
            }

            return null;
        }

        public string GetBlockPath(PlcBlock block)
        {
            if (block == null)
            {
                return string.Empty;
            }

            if (block.Parent is PlcBlockGroup parentGroup)
            {
                var groupPath = GetPlcBlockGroupPath(parentGroup);
                return string.IsNullOrEmpty(groupPath) ? block.Name : $"{groupPath}/{block.Name}";
            }

            return block.Name;
        }

        #region block code

        /// <summary>
        /// Exports a block and returns its code: reconstructed source text for SCL/STL,
        /// or a per-network instruction/operand summary for LAD/FBD/GRAPH.
        /// </summary>
        // Replaces characters that are illegal in Windows file names (e.g. ':' '/' '\\')
        // so block/type names like "Norm:20ftWS" can be exported to a file.
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_";
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        // Sanitizes each segment of a '/'-separated group path and joins them as a
        // Windows relative path, so group names with illegal characters still work.
        private static string SanitizeRelativePath(string groupPath)
        {
            if (string.IsNullOrEmpty(groupPath)) return string.Empty;
            var segments = groupPath
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(SanitizeFileName);
            return string.Join("\\", segments);
        }

        // Openness is not safe under concurrent access - parallel imports corrupt the
        // attached session (observed as the project "detaching" mid-build). Mutating and
        // heavy operations are funneled through this gate at the tool layer, so parallel
        // MCP calls queue instead of interleaving. Tools never call other tools, so the
        // non-reentrant semaphore cannot self-deadlock.
        private static readonly System.Threading.SemaphoreSlim _operationGate = new System.Threading.SemaphoreSlim(1, 1);

        public static T Serialized<T>(Func<T> action, string operation)
        {
            if (!_operationGate.Wait(TimeSpan.FromMinutes(5)))
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    $"'{operation}' waited 5 minutes for another operation to finish and gave up. A previous call is most likely stuck on a TIA dialog - check TIA Portal.");
            }
            try
            {
                return action();
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public static void Serialized(Action action, string operation)
        {
            Serialized<object?>(() => { action(); return null; }, operation);
        }

        /// <summary>
        /// Joins the messages of an exception chain so the real Openness reason
        /// (often two levels deep) survives into the tool error.
        /// </summary>
        public static string DescribeException(Exception ex)
        {
            var parts = new List<string>();
            var current = ex;
            while (current != null && parts.Count < 5)
            {
                if (!string.IsNullOrWhiteSpace(current.Message) && !parts.Contains(current.Message))
                {
                    parts.Add(current.Message);
                }
                current = current.InnerException;
            }
            return string.Join(" <- ", parts);
        }

        /// <summary>
        /// Runs a potentially-blocking Openness operation on a worker thread with a timeout.
        /// If TIA Portal raises a modal dialog Openness cannot dismiss (an address/overwrite
        /// confirmation, or the object being open in an editor), the underlying call blocks
        /// indefinitely; this returns a fast, clear error instead of hanging the client.
        /// </summary>
        public static T RunWithTimeout<T>(Func<T> action, int timeoutSeconds, string operation)
        {
            var task = System.Threading.Tasks.Task.Run(action);
            if (!task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    $"'{operation}' did not complete within {timeoutSeconds}s. TIA Portal is most likely waiting on a modal dialog that Openness cannot dismiss (e.g. an address/overwrite confirmation, or the object is open in an editor). Check TIA Portal, dismiss any dialog and/or close the relevant editor, then retry.");
            }
            try
            {
                return task.Result;
            }
            catch (System.AggregateException ae)
            {
                throw ae.InnerException ?? ae;
            }
        }

        public (string Language, string Code) GetBlockCode(string softwarePath, string blockPath)
        {
            _logger?.LogInformation($"Getting block code: {blockPath}");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "tia_blockcode_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var block = GetBlock(softwarePath, blockPath);
                if (block == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Block not found at '{blockPath}'");
                }

                if (!block.IsConsistent)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Block is inconsistent (not compiled); compile it before reading its code.");
                }

                var lang = Enum.GetName(typeof(ProgrammingLanguage), block.ProgrammingLanguage) ?? block.ProgrammingLanguage.ToString();

                // Use a filesystem-safe fixed filename. Block names may contain ':' or '/'
                // (e.g. "Norm:PresureSwitchPN7071"), which are illegal in Windows paths and
                // would otherwise make the export fail. The temp dir is unique per call.
                var file = Path.Combine(tempDir, "block.xml");
                try
                {
                    block.Export(new FileInfo(file), ExportOptions.None);
                }
                catch (Exception ex)
                {
                    throw new PortalException(PortalErrorCode.ExportFailed, $"Failed to export block '{blockPath}' for code reconstruction: {ex.Message}", null, ex);
                }

                var doc = XDocument.Load(file);
                var code = ReconstructBlockCode(doc, lang);
                return (lang, code);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private string ReconstructBlockCode(XDocument doc, string fallbackLang)
        {
            var compileUnits = doc.Descendants().Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit").ToList();
            if (compileUnits.Count == 0)
            {
                // No networks: this is a data block (DB) or interface-only block.
                // Reconstruct its interface (data structure) instead.
                var iface = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Interface");
                if (iface != null)
                {
                    var dbSb = new StringBuilder();
                    dbSb.AppendLine("// Data block interface (no executable code)");
                    var any = false;
                    foreach (var section in iface.Descendants().Where(e => e.Name.LocalName == "Section"))
                    {
                        if (!section.Elements().Any(e => e.Name.LocalName == "Member")) continue;
                        var sectionName = section.Attribute("Name")?.Value ?? "Section";
                        dbSb.AppendLine($"{sectionName}:");
                        ReconstructTypeMembers(section, dbSb, 1);
                        any = true;
                    }
                    if (any) return dbSb.ToString();
                }
                return "(No code networks or interface members found in this block.)";
            }

            var sb = new StringBuilder();
            int networkNumber = 0;
            foreach (var cu in compileUnits)
            {
                networkNumber++;
                var lang = cu.Descendants().FirstOrDefault(e => e.Name.LocalName == "ProgrammingLanguage")?.Value ?? fallbackLang;

                var structuredText = cu.Descendants().FirstOrDefault(e => e.Name.LocalName == "StructuredText");
                var flgNet = cu.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");

                if (structuredText != null)
                {
                    // Text languages (SCL / STL): reconstruct the source verbatim.
                    // In multi-network blocks (e.g. a LAD block with embedded SCL networks),
                    // add a header so each network's source is clearly delimited and titled.
                    if (compileUnits.Count > 1)
                    {
                        var stTitle = GetMultilingualText(cu, "Title");
                        var stComment = GetMultilingualText(cu, "Comment");
                        sb.AppendLine();
                        sb.AppendLine($"// ===== Network {networkNumber}{(string.IsNullOrEmpty(stTitle) ? "" : ": " + stTitle)} [{lang}] =====");
                        if (!string.IsNullOrEmpty(stComment))
                        {
                            sb.AppendLine($"// {stComment}");
                        }
                    }

                    sb.Append(ReconstructStructuredText(structuredText));
                    if (sb.Length > 0 && sb[sb.Length - 1] != '\n')
                    {
                        sb.AppendLine();
                    }
                }
                else if (flgNet != null)
                {
                    // Graphical languages (LAD / FBD): summarize each network.
                    var title = GetMultilingualText(cu, "Title");
                    var comment = GetMultilingualText(cu, "Comment");
                    sb.AppendLine();
                    sb.AppendLine($"// ===== Network {networkNumber}{(string.IsNullOrEmpty(title) ? "" : ": " + title)} [{lang}] =====");
                    if (!string.IsNullOrEmpty(comment))
                    {
                        sb.AppendLine($"// {comment}");
                    }
                    sb.Append(SummarizeFlgNet(flgNet));
                }
            }

            return sb.ToString();
        }

        private string ReconstructStructuredText(XElement st)
        {
            var sb = new StringBuilder();
            AppendStTokens(st, sb);
            return sb.ToString();
        }

        private void AppendStTokens(XElement parent, StringBuilder sb)
        {
            foreach (var el in parent.Elements())
            {
                switch (el.Name.LocalName)
                {
                    case "Token":
                        sb.Append(el.Attribute("Text")?.Value);
                        break;
                    case "Blank":
                        {
                            int n = int.TryParse(el.Attribute("Num")?.Value, out var b) ? b : 1;
                            sb.Append(new string(' ', n));
                            break;
                        }
                    case "NewLine":
                        {
                            int n = int.TryParse(el.Attribute("Num")?.Value, out var nl) ? nl : 1;
                            for (int i = 0; i < n; i++) sb.Append("\r\n");
                            break;
                        }
                    case "Access":
                        sb.Append(ReconstructAccess(el));
                        break;
                    case "Comment":
                    case "LineComment":
                        {
                            var text = el.Descendants().FirstOrDefault(d => d.Name.LocalName == "Text")?.Value ?? el.Value;
                            sb.Append(el.Name.LocalName == "LineComment" ? "//" + text : "(*" + text + "*)");
                            break;
                        }
                    default:
                        // Unknown container element: descend so nested tokens are not lost.
                        AppendStTokens(el, sb);
                        break;
                }
            }
        }

        private string ReconstructAccess(XElement access)
        {
            // Literal / typed constant
            var constant = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Constant")
                ?? access.Descendants().FirstOrDefault(e => e.Name.LocalName == "Constant");
            if (constant != null)
            {
                var val = constant.Descendants().FirstOrDefault(e => e.Name.LocalName == "ConstantValue")?.Value;
                if (!string.IsNullOrEmpty(val)) return val;
                // constant name (named constant access)
                var cname = constant.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(cname)) return cname;
            }

            // Symbolic operand: join component names with '.', keeping array indices
            var symbol = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Symbol")
                ?? access.Descendants().FirstOrDefault(e => e.Name.LocalName == "Symbol");
            if (symbol != null)
            {
                var parts = symbol.Elements()
                    .Where(e => e.Name.LocalName == "Component")
                    .Select(ReconstructComponent)
                    .Where(s => s.Length > 0)
                    .ToList();
                if (parts.Count > 0) return string.Join(".", parts);
            }

            // Block / instruction call (Access Scope="Call"): faithful rendering with
            // instance and actual parameters - these used to be dropped entirely.
            var callInfo = access.Elements().FirstOrDefault(e => e.Name.LocalName == "CallInfo");
            if (callInfo != null)
            {
                return ReconstructCallInfo(callInfo, isInstruction: false);
            }

            var instruction = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Instruction");
            if (instruction != null)
            {
                return ReconstructCallInfo(instruction, isInstruction: true);
            }

            // SCL expression (e.g. an index or parenthesized term): nested token stream
            var expression = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Expression");
            if (expression != null)
            {
                var sb = new StringBuilder();
                AppendStTokens(expression, sb);
                return sb.ToString();
            }

            var predefined = access.Elements().FirstOrDefault(e => e.Name.LocalName == "PredefinedVariable");
            if (predefined != null)
            {
                return predefined.Attribute("Name")?.Value ?? "";
            }

            var label = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Label");
            if (label != null)
            {
                return label.Attribute("Name")?.Value ?? "";
            }

            return string.Empty;
        }

        // A symbol component: name plus optional array indices ('Words[0]'), which are
        // serialized as child Access elements.
        private string ReconstructComponent(XElement component)
        {
            var name = component.Attribute("Name")?.Value ?? "";
            var indices = component.Elements()
                .Where(e => e.Name.LocalName == "Access")
                .Select(ReconstructAccess)
                .Where(s => s.Length > 0)
                .ToList();
            if (indices.Count > 0)
            {
                name += "[" + string.Join(", ", indices) + "]";
            }
            return name;
        }

        // CallInfo (user block call) / Instruction (system instruction call): render the
        // callee (instance DB for FBs, otherwise the block/instruction name) followed by
        // the original token stream - parentheses, parameter names, ':=' and operands all
        // survive, so calls are no longer reconstructed as bare statement numbers.
        private string ReconstructCallInfo(XElement callInfo, bool isInstruction)
        {
            var sb = new StringBuilder();

            var instance = callInfo.Elements().FirstOrDefault(e => e.Name.LocalName == "Instance");
            if (instance != null)
            {
                var parts = instance.Elements()
                    .Where(e => e.Name.LocalName == "Component")
                    .Select(ReconstructComponent)
                    .Where(s => s.Length > 0)
                    .ToList();
                sb.Append(parts.Count > 0 ? string.Join(".", parts) : callInfo.Attribute("Name")?.Value ?? "");
            }
            else
            {
                sb.Append(callInfo.Attribute("Name")?.Value ?? "");
            }

            var hasParenToken = callInfo.Elements().Any(e => e.Name.LocalName == "Token" && (e.Attribute("Text")?.Value ?? "").Contains("("));
            if (!hasParenToken)
            {
                sb.Append('(');
            }

            var first = true;
            foreach (var child in callInfo.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "Instance":
                    case "IntegerAttribute":
                    case "DateAttribute":
                    case "BooleanAttribute":
                        break;
                    case "Token":
                        sb.Append(child.Attribute("Text")?.Value);
                        first = false;
                        break;
                    case "Blank":
                        {
                            int n = int.TryParse(child.Attribute("Num")?.Value, out var b) ? b : 1;
                            sb.Append(new string(' ', n));
                            break;
                        }
                    case "NewLine":
                        sb.Append("\r\n");
                        break;
                    case "Parameter":
                        if (!hasParenToken && !first)
                        {
                            sb.Append(", ");
                        }
                        sb.Append(child.Attribute("Name")?.Value);
                        if (!child.Descendants().Any(e => e.Name.LocalName == "Token" && (e.Attribute("Text")?.Value ?? "").Contains(":=")))
                        {
                            sb.Append(" := ");
                        }
                        AppendStTokens(child, sb);
                        first = false;
                        break;
                    case "NamelessParameter":
                        if (!hasParenToken && !first)
                        {
                            sb.Append(", ");
                        }
                        AppendStTokens(child, sb);
                        first = false;
                        break;
                    default:
                        AppendStTokens(child, sb);
                        first = false;
                        break;
                }
            }

            if (!hasParenToken)
            {
                sb.Append(')');
            }

            return sb.ToString();
        }

        private string SummarizeFlgNet(XElement flgNet)
        {
            // 1) Map each Access UId to its reconstructed operand text.
            var accessByUid = new Dictionary<string, string>();
            foreach (var acc in flgNet.Descendants().Where(e => e.Name.LocalName == "Access"))
            {
                var uid = acc.Attribute("UId")?.Value;
                if (!string.IsNullOrEmpty(uid) && !accessByUid.ContainsKey(uid))
                {
                    accessByUid[uid] = ReconstructAccess(acc);
                }
            }

            // 2) From wires, attach operands to the part/call pins they feed.
            // A wire that links an IdentCon (an Access) to a NameCon (a part pin)
            // means that operand is wired to that named pin.
            var pinsByPart = new Dictionary<string, List<string>>();
            foreach (var wire in flgNet.Descendants().Where(e => e.Name.LocalName == "Wire"))
            {
                var connectors = wire.Elements().ToList();

                string operand = null;
                foreach (var c in connectors.Where(c => c.Name.LocalName == "IdentCon"))
                {
                    var au = c.Attribute("UId")?.Value;
                    if (au != null && accessByUid.TryGetValue(au, out var op))
                    {
                        operand = op;
                    }
                }
                if (operand == null) continue;

                foreach (var nc in connectors.Where(c => c.Name.LocalName == "NameCon"))
                {
                    var pu = nc.Attribute("UId")?.Value;
                    var pin = nc.Attribute("Name")?.Value;
                    if (string.IsNullOrEmpty(pu)) continue;
                    if (!pinsByPart.TryGetValue(pu, out var list))
                    {
                        list = new List<string>();
                        pinsByPart[pu] = list;
                    }
                    list.Add($"{pin}={operand}");
                }
            }

            // 3) Emit one line per instruction (Part/Call) in document order,
            // annotated with the operands wired to its pins.
            var sb = new StringBuilder();
            var parts = flgNet.Descendants().FirstOrDefault(e => e.Name.LocalName == "Parts");
            if (parts == null)
            {
                sb.AppendLine("//   (empty network)");
                return sb.ToString();
            }

            foreach (var el in parts.Elements())
            {
                var uid = el.Attribute("UId")?.Value;
                string label;
                if (el.Name.LocalName == "Part")
                {
                    label = el.Attribute("Name")?.Value ?? "Part";
                }
                else if (el.Name.LocalName == "Call")
                {
                    var ci = el.Descendants().FirstOrDefault(e => e.Name.LocalName == "CallInfo");
                    label = $"CALL {ci?.Attribute("BlockType")?.Value} \"{ci?.Attribute("Name")?.Value}\"";
                }
                else
                {
                    continue; // skip Access and other non-instruction elements
                }

                if (uid != null && pinsByPart.TryGetValue(uid, out var pins) && pins.Count > 0)
                {
                    sb.AppendLine($"//   {label}({string.Join(", ", pins)})");
                }
                else
                {
                    sb.AppendLine($"//   {label}");
                }
            }

            return sb.ToString();
        }

        private string GetMultilingualText(XElement compileUnit, string compositionName)
        {
            // Network Title/Comment live directly under the CompileUnit's ObjectList,
            // not inside the NetworkSource (which may hold per-element comments).
            var objectList = compileUnit.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
            var scope = objectList ?? compileUnit;

            var mlt = scope.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "MultilingualText" &&
                (string)e.Attribute("CompositionName") == compositionName);
            if (mlt == null) return string.Empty;

            var text = mlt.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text");
            return text?.Value?.Trim() ?? string.Empty;
        }

        /// <summary>
        /// Exports a PLC data type (UDT) and reconstructs its definition as readable text.
        /// </summary>
        public string GetTypeCode(string softwarePath, string typePath)
        {
            _logger?.LogInformation($"Getting type code: {typePath}");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var type = GetType(softwarePath, typePath);
            if (type == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"Type not found at '{typePath}'");
            }

            if (!type.IsConsistent)
            {
                throw new PortalException(PortalErrorCode.InvalidState, "Type is inconsistent (not compiled); compile it before reading its definition.");
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "tia_typecode_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var file = Path.Combine(tempDir, "type.xml");
                try
                {
                    type.Export(new FileInfo(file), ExportOptions.None);
                }
                catch (Exception ex)
                {
                    throw new PortalException(PortalErrorCode.ExportFailed, $"Failed to export type '{typePath}': {ex.Message}", null, ex);
                }

                var doc = XDocument.Load(file);
                return ReconstructTypeDefinition(doc, type.Name);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private string ReconstructTypeDefinition(XDocument doc, string typeName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"TYPE \"{typeName}\"");
            sb.AppendLine("   STRUCT");

            var interfaceEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Interface");
            var section = interfaceEl?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Section");
            if (section != null)
            {
                ReconstructTypeMembers(section, sb, 2);
            }

            sb.AppendLine("   END_STRUCT;");
            sb.AppendLine("END_TYPE");
            return sb.ToString();
        }

        private void ReconstructTypeMembers(XElement parent, StringBuilder sb, int indentLevel)
        {
            var indent = new string(' ', indentLevel * 3);
            foreach (var member in parent.Elements().Where(e => e.Name.LocalName == "Member"))
            {
                var name = member.Attribute("Name")?.Value ?? "";
                var dt = member.Attribute("Datatype")?.Value ?? "";
                var nested = member.Elements().Where(e => e.Name.LocalName == "Member").ToList();

                if (nested.Count > 0 && dt.StartsWith("Struct", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"{indent}{name} : Struct");
                    ReconstructTypeMembers(member, sb, indentLevel + 1);
                    sb.AppendLine($"{indent}END_STRUCT;");
                }
                else
                {
                    var start = member.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue")?.Value;
                    var startText = string.IsNullOrEmpty(start) ? "" : $" := {start}";
                    sb.AppendLine($"{indent}{name} : {dt}{startText};");
                }
            }
        }

        /// <summary>
        /// Returns the raw SimaticML (Openness) XML of a block, for read-modify-write round-tripping.
        /// </summary>
        public string GetBlockXml(string softwarePath, string blockPath)
        {
            _logger?.LogInformation($"Getting block XML: {blockPath}");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var block = GetBlock(softwarePath, blockPath);
            if (block == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"Block not found at '{blockPath}'");
            }
            if (!block.IsConsistent)
            {
                throw new PortalException(PortalErrorCode.InvalidState, "Block is inconsistent (not compiled); compile it before exporting its XML.");
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "tia_blockxml_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var file = Path.Combine(tempDir, "block.xml");
                block.Export(new FileInfo(file), ExportOptions.None);
                return File.ReadAllText(file);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// Creates or replaces a block from SCL source text: imports the source, generates the
        /// block, removes the transient external source, and optionally compiles. MUTATES the project.
        /// </summary>
        public (List<string> AffectedBlocks, bool Compiled, string CompileSummary) WriteBlockScl(string softwarePath, string sclSource, bool compile, bool overwrite, string groupPath = "")
        {
            _logger?.LogInformation("Writing block(s) from SCL source...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }
            if (string.IsNullOrWhiteSpace(sclSource))
            {
                throw new PortalException(PortalErrorCode.InvalidParams, "SCL source is empty");
            }

            var before = new HashSet<string>(GetBlocks(softwarePath).Select(b => b.Name), StringComparer.OrdinalIgnoreCase);

            // Determine declared block names from the SCL and refuse to clobber existing ones unless allowed.
            var declared = Regex.Matches(sclSource, "(?im)\\b(?:FUNCTION_BLOCK|FUNCTION|ORGANIZATION_BLOCK|DATA_BLOCK)\\s+\"?([A-Za-z0-9_]+)\"?")
                .Cast<Match>().Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!overwrite)
            {
                var clash = declared.Where(n => before.Contains(n)).ToList();
                if (clash.Any())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, $"Block(s) already exist: {string.Join(", ", clash)}. Pass overwrite=true to replace.");
                }
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "tia_writescl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            const string srcName = "mcp_write.scl";
            var srcFile = Path.Combine(tempDir, srcName);
            try
            {
                File.WriteAllText(srcFile, sclSource);

                if (!ImportExternalSource(softwarePath, "", srcFile))
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Failed to import the SCL as an external source (check the SCL syntax/headers).");
                }

                bool generated;
                try
                {
                    generated = GenerateBlocksFromSource(softwarePath, srcName);
                }
                finally
                {
                    try { DeleteExternalSource(softwarePath, srcName); } catch { }
                }

                if (!generated)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Failed to generate blocks from the SCL source (syntax error or unsupported construct).");
                }

                var after = GetBlocks(softwarePath);
                var affected = after.Where(b => !before.Contains(b.Name)).Select(b => b.Name).ToList();
                foreach (var d in declared)
                {
                    if (!affected.Contains(d, StringComparer.OrdinalIgnoreCase) &&
                        after.Any(b => b.Name.Equals(d, StringComparison.OrdinalIgnoreCase)))
                    {
                        affected.Add(d);
                    }
                }

                bool compiled = false;
                string summary = "(not compiled)";

                // relocation goes through XML export, which needs consistent blocks
                var mustCompile = compile || (!string.IsNullOrWhiteSpace(groupPath) && affected.Count > 0);
                if (mustCompile)
                {
                    var result = CompileSoftware(softwarePath);
                    if (result != null)
                    {
                        var msgs = new List<string>();
                        CollectCompileMessages(result.Messages, msgs);
                        int errs = msgs.Count(m => m.Contains("[Error]"));
                        int warns = msgs.Count(m => m.Contains("[Warning]"));
                        compiled = result.State.ToString() != "Error";
                        summary = $"{(compiled ? "SUCCESS" : "FAILED")}: {errs} error(s), {warns} warning(s)";
                        if (!compile)
                        {
                            summary += " (auto-compiled to allow groupPath placement)";
                        }
                    }
                }

                // generated blocks land at the program-blocks root; place them if requested
                if (!string.IsNullOrWhiteSpace(groupPath) && affected.Count > 0)
                {
                    if (!compiled)
                    {
                        summary += $" - blocks NOT moved to '{groupPath}' (compile failed; the move needs consistent blocks)";
                    }
                    else
                    {
                        var moved = new List<string>();
                        var moveErrors = new List<string>();
                        foreach (var name in affected)
                        {
                            try
                            {
                                MoveBlock(softwarePath, name, groupPath);
                                moved.Add(name);
                            }
                            catch (Exception mex)
                            {
                                moveErrors.Add($"{name}: {mex.Message}");
                            }
                        }
                        summary += $" - moved {moved.Count}/{affected.Count} block(s) to '{groupPath}'";
                        if (moveErrors.Count > 0)
                        {
                            summary += $" (errors: {string.Join("; ", moveErrors)})";
                        }
                    }
                }

                return (affected, compiled, summary);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// Creates or replaces a block from SimaticML (Openness) XML text - the read-modify-write
        /// path for any language including LAD. MUTATES the project.
        /// </summary>
        public List<string> WriteBlockXml(string softwarePath, string groupPath, string blockXml, bool overwrite)
        {
            _logger?.LogInformation("Writing block from SimaticML XML...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }
            if (string.IsNullOrWhiteSpace(blockXml))
            {
                throw new PortalException(PortalErrorCode.InvalidParams, "Block XML is empty");
            }

            string blockName;
            try
            {
                var doc = XDocument.Parse(blockXml);
                var blockEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks."));
                blockName = blockEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList")?
                    .Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.InvalidParams, $"Block XML is not valid: {ex.Message}", null, ex);
            }

            if (!overwrite && !string.IsNullOrEmpty(blockName))
            {
                if (GetBlocks(softwarePath).Any(b => b.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new PortalException(PortalErrorCode.InvalidState, $"Block '{blockName}' already exists. Pass overwrite=true to replace.");
                }
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "tia_writexml_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var file = Path.Combine(tempDir, SanitizeFileName(blockName ?? "block") + ".xml");
            try
            {
                File.WriteAllText(file, blockXml, new UTF8Encoding(true));
                if (!ImportBlock(softwarePath, groupPath ?? string.Empty, file))
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Import of the block XML failed (check the SimaticML is valid and the target group exists).");
                }
                return new List<string> { blockName ?? "(imported)" };
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// Returns the raw SimaticML (Openness) XML of a PLC data type (UDT), for round-tripping.
        /// </summary>
        public string GetTypeXml(string softwarePath, string typePath)
        {
            _logger?.LogInformation($"Getting type XML: {typePath}");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var type = GetType(softwarePath, typePath);
            if (type == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"Type not found at '{typePath}'");
            }
            if (!type.IsConsistent)
            {
                throw new PortalException(PortalErrorCode.InvalidState, "Type is inconsistent (not compiled); compile it before exporting its XML.");
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "tia_typexml_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var file = Path.Combine(tempDir, "type.xml");
                type.Export(new FileInfo(file), ExportOptions.None);
                return File.ReadAllText(file);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// Creates or replaces a PLC data type (UDT) from SimaticML XML text. MUTATES the project.
        /// </summary>
        public List<string> WriteTypeXml(string softwarePath, string groupPath, string typeXml, bool overwrite)
        {
            _logger?.LogInformation("Writing UDT from SimaticML XML...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }
            if (string.IsNullOrWhiteSpace(typeXml))
            {
                throw new PortalException(PortalErrorCode.InvalidParams, "Type XML is empty");
            }

            string typeName;
            try
            {
                var doc = XDocument.Parse(typeXml);
                var typeEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Types."));
                typeName = typeEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList")?
                    .Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.InvalidParams, $"Type XML is not valid: {ex.Message}", null, ex);
            }

            if (!overwrite && !string.IsNullOrEmpty(typeName))
            {
                if (GetTypes(softwarePath).Any(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new PortalException(PortalErrorCode.InvalidState, $"Type '{typeName}' already exists. Pass overwrite=true to replace.");
                }
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "tia_writetypexml_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var file = Path.Combine(tempDir, SanitizeFileName(typeName ?? "type") + ".xml");
            try
            {
                File.WriteAllText(file, typeXml, new UTF8Encoding(true));
                if (!ImportType(softwarePath, groupPath ?? string.Empty, file))
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Import of the type XML failed (check the SimaticML is valid and the target group exists).");
                }
                return new List<string> { typeName ?? "(imported)" };
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// Creates or replaces PLC data type(s) (UDT) from SCL "TYPE ... END_TYPE" source text,
        /// via an external source. MUTATES the project.
        /// </summary>
        public (List<string> AffectedTypes, bool Compiled, string CompileSummary) WriteTypeScl(string softwarePath, string sclSource, bool compile, bool overwrite)
        {
            _logger?.LogInformation("Writing UDT(s) from SCL TYPE source...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }
            if (string.IsNullOrWhiteSpace(sclSource))
            {
                throw new PortalException(PortalErrorCode.InvalidParams, "SCL/UDT source is empty");
            }

            var before = new HashSet<string>(GetTypes(softwarePath).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

            var declared = Regex.Matches(sclSource, "(?im)\\bTYPE\\s+\"?([A-Za-z0-9_]+)\"?")
                .Cast<Match>().Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!overwrite)
            {
                var clash = declared.Where(n => before.Contains(n)).ToList();
                if (clash.Any())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, $"Type(s) already exist: {string.Join(", ", clash)}. Pass overwrite=true to replace.");
                }
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "tia_writetypescl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            const string srcName = "mcp_type.udt";
            var srcFile = Path.Combine(tempDir, srcName);
            try
            {
                File.WriteAllText(srcFile, sclSource);

                if (!ImportExternalSource(softwarePath, "", srcFile))
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Failed to import the UDT source (check the TYPE/END_TYPE syntax).");
                }

                bool generated;
                try
                {
                    generated = GenerateBlocksFromSource(softwarePath, srcName);
                }
                finally
                {
                    try { DeleteExternalSource(softwarePath, srcName); } catch { }
                }

                if (!generated)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Failed to generate the UDT from source.");
                }

                var after = GetTypes(softwarePath);
                var affected = after.Where(t => !before.Contains(t.Name)).Select(t => t.Name).ToList();
                foreach (var d in declared)
                {
                    if (!affected.Contains(d, StringComparer.OrdinalIgnoreCase) &&
                        after.Any(t => t.Name.Equals(d, StringComparison.OrdinalIgnoreCase)))
                    {
                        affected.Add(d);
                    }
                }

                bool compiled = false;
                string summary = "(not compiled)";
                if (compile)
                {
                    var result = CompileSoftware(softwarePath);
                    if (result != null)
                    {
                        var msgs = new List<string>();
                        CollectCompileMessages(result.Messages, msgs);
                        int errs = msgs.Count(m => m.Contains("[Error]"));
                        int warns = msgs.Count(m => m.Contains("[Warning]"));
                        compiled = result.State.ToString() != "Error";
                        summary = $"{(compiled ? "SUCCESS" : "FAILED")}: {errs} error(s), {warns} warning(s)";
                    }
                }

                return (affected, compiled, summary);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        #endregion

        public List<PlcBlock> GetBlocks(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting blocks...");

            if (IsProjectNull())
            {
                return [];
            }

            var list = new List<PlcBlock>();

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var group = plcSoftware?.BlockGroup;

                    if (group != null)
                    {
                        GetBlocksRecursive(group, list, regexName);
                    }
                }
            }
            catch (Exception)
            {
                // Console.WriteLine($"Error getting blocks: {ex.Message}");
            }

            return list;
        }

        public PlcBlockGroup? GetBlockRootGroup(string softwarePath)
        {
            _logger?.LogInformation("Getting block root group...");

            if (IsProjectNull())
            {
                return null;
            }

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    return plcSoftware.BlockGroup;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting block root group");
            }

            return null;
        }

        public List<PlcType> GetTypes(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting types...");

            if (IsProjectNull())
            {
                return [];
            }

            var list = new List<PlcType>();

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var group = plcSoftware?.TypeGroup;

                    if (group != null)
                    {
                        GetTypesRecursive(group, list, regexName);
                    }
                }
            }
            catch (Exception)
            {
                // Console.WriteLine($"Error getting user defined types: {ex.Message}");
            }

            return list;
        }

        public PlcBlock? ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {
            _logger?.LogInformation($"Exporting block by path: {blockPath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var block = GetBlock(softwarePath, blockPath);

                if (block == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, "Block not found");
                }

                if (preservePath)
                {
                    var groupPath = "";
                    if (block.Parent is PlcBlockGroup parentGroup)
                    {
                        groupPath = GetPlcBlockGroupPath(parentGroup);
                    }

                    exportPath = Path.Combine(exportPath, SanitizeRelativePath(groupPath), $"{SanitizeFileName(block.Name)}.xml");
                }
                else
                {
                    exportPath = Path.Combine(exportPath, $"{SanitizeFileName(block.Name)}.xml");
                }

                // TIA Portal never exports inconsistent blocks
                if (!block.IsConsistent)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Block is inconsistent; TIA Portal does not export inconsistent blocks.");
                }

                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }

                block.Export(new FileInfo(exportPath), ExportOptions.None);

                return block;
            }
            catch (Exception ex)
            {
                //If the exception is already a PortalException, use it; otherwise, wrap it in a new PortalException
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath"] = blockPath;
                pex.Data["exportPath"] = exportPath;

                _logger?.LogError(pex, "ExportBlock failed for {SoftwarePath} {BlockPath} -> {ExportPath}", softwarePath, blockPath, exportPath);
                throw pex;
            }
        }

        public PlcType? ExportType(string softwarePath, string typePath, string exportPath, bool preservePath = false)
        {
            _logger?.LogInformation($"Exporting type by path: {typePath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var type = GetType(softwarePath, typePath);

                if (type == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, "Type not found");
                }

                // TIA Portal never exports inconsistent types
                if (!type.IsConsistent)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Type is inconsistent; TIA Portal does not export inconsistent types.");
                }

                if (preservePath)
                {
                    var groupPath = "";
                    if (type.Parent is PlcTypeGroup parentGroup)
                    {
                        groupPath = GetPlcTypeGroupPath(parentGroup);
                    }

                    exportPath = Path.Combine(exportPath, SanitizeRelativePath(groupPath), $"{SanitizeFileName(type.Name)}.xml");
                }
                else
                {
                    exportPath = Path.Combine(exportPath, $"{SanitizeFileName(type.Name)}.xml");
                }

                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }

                type.Export(new FileInfo(exportPath), ExportOptions.None);

                return type;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                if (!pex.Data.Contains("softwarePath")) pex.Data["softwarePath"] = softwarePath;
                if (!pex.Data.Contains("typePath")) pex.Data["typePath"] = typePath;
                if (!pex.Data.Contains("exportPath")) pex.Data["exportPath"] = exportPath;

                _logger?.LogError(pex, "ExportType failed for {SoftwarePath} {TypePath} -> {ExportPath}", softwarePath, typePath, exportPath);
                throw pex;
            }
        }

        public bool ImportBlock(string softwarePath, string groupPath, string importPath)
        {
            _logger?.LogInformation($"Importing block from path: {importPath}");

            if (IsProjectNull())
            {
                return false;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var blockGroup = plcSoftware?.BlockGroup;

                if (blockGroup != null)
                {

                    var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                    if (group == null)
                    {
                        return false;
                    }

                    try
                    {
                        // Correct the argument type by using FileInfo instead of FileStream
                        var fileInfo = new FileInfo(importPath);
                        if (fileInfo.Exists)
                        {
                            var list = group.Blocks.Import(fileInfo, ImportOptions.Override);
                            if (list != null && list.Count > 0)
                            {
                                return true;
                            }
                        }

                    }
                    catch (Exception ex)
                    {
                        throw new PortalException(PortalErrorCode.InvalidParams, $"Block import from '{importPath}' failed: {DescribeException(ex)}", null, ex);
                    }
                }
            }

            return false;
        }

        public bool ImportType(string softwarePath, string groupPath, string importPath)
        {
            _logger?.LogInformation($"Importing type from path: {importPath}");

            var success = false;

            if (IsProjectNull())
            {
                return success;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var typeGroup = plcSoftware?.TypeGroup;

                if (typeGroup != null)
                {
                    var group = GetPlcTypeGroupByPath(softwarePath, groupPath);
                    if (group == null)
                    {
                        return false;
                    }

                    try
                    {
                        // Correct the argument type by using FileInfo instead of FileStream
                        var fileInfo = new FileInfo(importPath);
                        if (fileInfo.Exists)
                        {
                            var list = group.Types.Import(fileInfo, ImportOptions.Override);
                            if (list != null && list.Count > 0)
                            {
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new PortalException(PortalErrorCode.InvalidParams, $"Type import from '{importPath}' failed: {DescribeException(ex)}", null, ex);
                    }
                }
            }

            return success;
        }

        public IEnumerable<PlcBlock>? ExportBlocks(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _logger?.LogInformation("Exporting blocks...");

            if (IsProjectNull())
            {
                return null;
            }

            var exportList = new List<PlcBlock>();
            var failures = new List<string>();
            
            PlcBlock[] list;

            try
            {
                list = GetBlocks(softwarePath, regexName).ToArray();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to retrieve block list for {SoftwarePath}", softwarePath);
                return exportList;
            }

            for (int k = 0; k < list.Count(); k++)
            {
                var block = list[k];

                _logger?.LogDebug($"- Exporting block {k}/{list.Count()} : {block.Name}");

                string path;
                if (preservePath)
                {
                    var groupPath = "";
                    if (block.Parent is PlcBlockGroup parentGroup)
                    {
                        groupPath = GetPlcBlockGroupPath(parentGroup);
                    }
                    path = Path.Combine(exportPath, SanitizeRelativePath(groupPath), $"{SanitizeFileName(block.Name)}.xml");
                }
                else
                {
                    path = Path.Combine(exportPath, $"{SanitizeFileName(block.Name)}.xml");
                }

                try
                {
                    if (!block.IsConsistent)
                    {
                        _logger?.LogWarning("Skipping inconsistent block {Name}", block.Name);

                        continue;
                    }

                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (File.Exists(path))
                    {
                        try { File.Delete(path); }
                        catch (Exception ioEx)
                        {
                            failures.Add($"{block.Name}: cannot delete existing file ({ioEx.Message})");
                            _logger?.LogError(ioEx, "Delete failed for {File}", path);

                            continue;
                        }
                    }

                    try
                    {
                        block.Export(new FileInfo(path), ExportOptions.None);
                    }
                    catch (LicenseNotFoundException licEx)
                    {
                        failures.Add($"{block.Name}: license not found ({licEx.Message})");
                        _logger?.LogError(licEx, "License issue exporting {Block}", block.Name);

                        continue;
                    }
                    catch (EngineeringTargetInvocationException engEx)
                    {
                        failures.Add($"{block.Name}: target invocation failed ({engEx.Message})");
                        _logger?.LogError(engEx, "TargetInvocationException exporting {Block}", block.Name);

                        continue;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{block.Name}: export failed ({ex.Message})");
                        _logger?.LogError(ex, "Export failed for {Block}", block.Name);

                        continue;
                    }

                    exportList.Add(block);
                }
                catch (Exception ex)
                {
                    // Catch only truly unexpected wrapper-level errors
                    failures.Add($"{block.Name}: unexpected exception ({ex.Message})");
                    _logger?.LogError(ex, "Unexpected error at block {Block}", block.Name);
                    // continue with next block
                }
            }

            if (failures.Count > 0)
            {
                _logger?.LogWarning($"ExportBlocks completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
                // Optionally: _logger?.LogDebug("All failures: {Failures}", string.Join("; ", failures));
            }
            else
            {
                _logger?.LogInformation($"ExportBlocks completed successfully. Exported {exportList.Count} blocks.");
            }

            return exportList;
        }

        public IEnumerable<PlcType>? ExportTypes(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _logger?.LogInformation("Exporting types...");

            if (IsProjectNull())
            {
                return null;
            }

            var exportList = new List<PlcType>();
            var failures = new List<string>();

            PlcType[] list;

            try
            {
                list = GetTypes(softwarePath, regexName).ToArray();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to retrieve type list for {SoftwarePath}", softwarePath);
                return exportList;
            }

            for (int i = 0; i < list.Count(); i++)
            {
                var type = list[i];

                _logger?.LogDebug("- Exporting type {Index}/{Total} : {Name}", i, list.Count(), type.Name);

                string path;
                if (preservePath)
                {
                    var groupPath = "";
                    if (type.Parent is PlcTypeGroup parentGroup)
                    {
                        groupPath = GetPlcTypeGroupPath(parentGroup);
                    }
                    path = Path.Combine(exportPath, SanitizeRelativePath(groupPath), $"{SanitizeFileName(type.Name)}.xml");
                }
                else
                {
                    path = Path.Combine(exportPath, $"{SanitizeFileName(type.Name)}.xml");
                }

                try
                {
                    if (!type.IsConsistent)
                    {
                        _logger?.LogWarning("Skipping inconsistent type {Name}", type.Name);
                        continue;
                    }

                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch (Exception ioEx)
                        {
                            failures.Add($"{type.Name}: cannot delete existing file ({ioEx.Message})");
                            _logger?.LogError(ioEx, "Delete failed for {File}", path);
                            continue;
                        }
                    }

                    try
                    {
                        type.Export(new FileInfo(path), ExportOptions.None);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{type.Name}: export failed ({ex.Message})");
                        _logger?.LogError(ex, "Export failed for type {Type}", type.Name);
                        continue;
                    }

                    exportList.Add(type);
                }
                catch (Exception ex)
                {
                    failures.Add($"{type.Name}: unexpected exception ({ex.Message})");
                    _logger?.LogError(ex, "Unexpected error at type {Type}", type.Name);
                }
            }

            if (failures.Count > 0)
            {
                _logger?.LogWarning($"ExportTypes completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
            }
            else
            {
                _logger?.LogInformation($"ExportTypes completed successfully. Exported {exportList.Count} types.");
            }

            return exportList;
        }
        

        public bool ExportAsDocuments(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {
            _logger?.LogInformation($"Exporting block as documents by path: {blockPath}");
            var success = false;
            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                if (Engineering.TiaMajorVersion < 20)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "ExportAsDocuments requires TIA Portal V20 or newer");
                }

                
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    if (plcSoftware != null)
                    {
                        // Export code blocks as documents
                        // https://docs.tia.siemens.cloud/r/en-us/v20/creating-and-managing-blocks/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500

                        var groupPath = blockPath.Contains("/") ? blockPath.Substring(0, blockPath.LastIndexOf("/")) : string.Empty;
                        var blockName = blockPath.Contains("/") ? blockPath.Substring(blockPath.LastIndexOf("/") + 1) : blockPath;

                        var group = GetPlcBlockGroupByPath(softwarePath, groupPath);

                        //group?.Blocks.ForEach(b => Console.WriteLine($"Block: {b.Name}, Type: {b.GetType().Name}"));

                        // join exportPath and groupPath
                        if (!Directory.Exists(exportPath))
                        {
                            Directory.CreateDirectory(exportPath);
                        }

                        if (preservePath && !string.IsNullOrEmpty(groupPath))
                        {
                            exportPath = Path.Combine(exportPath, groupPath);

                            if (!Directory.Exists(exportPath))
                            {
                                Directory.CreateDirectory(exportPath);
                            }
                        }

                        try
                        {
                            // delete files s7dcl/s7res if already exists
                            var blockFiles7dclPath = Path.Combine(exportPath, $"{blockName}.s7dcl");
                            if (File.Exists(blockFiles7dclPath))
                            {
                                File.Delete(blockFiles7dclPath);
                            }
                            var blockFiles7resPath = Path.Combine(exportPath, $"{blockName}.s7res");
                            if (File.Exists(blockFiles7resPath))
                            {
                                File.Delete(blockFiles7resPath);
                            }

                            var result = group?.Blocks.Find(blockName)?.ExportAsDocuments(new DirectoryInfo(exportPath), blockName);

                            if (result != null && result.State == DocumentResultState.Success)
                            {
                                success = true;
                            }
                        }
                        catch (EngineeringNotSupportedException ex)
                        {
                            // The export or import of blocks with mixed programming languages is not possible
                            throw new PortalException(PortalErrorCode.ExportFailed, $"EngineeringNotSupportedException at block '{blockName}'. {ex.Message}", null, ex);
                        }
                        catch (Exception ex)
                        {
                            throw new PortalException(PortalErrorCode.ExportFailed, $"Exception at block '{blockName}'. {ex.Message}", null, ex);
                        }

                    }

                }


            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath"] = blockPath;
                pex.Data["exportPath"] = exportPath;

                _logger?.LogError(pex, "ExportAsDocuments failed for {SoftwarePath} {BlockPath} -> {ExportPath}", softwarePath, blockPath, exportPath);
                throw pex;
            }
            return success;
        }

        // TIA portal crashes when exporting blocks as documents, :-(
        public (List<PlcBlock> Exported, List<string> Failures) ExportBlocksAsDocuments(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _logger?.LogInformation("Exporting blocks as documents...");

            var exportList = new List<PlcBlock>();
            var failures = new List<string>();

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            if (Engineering.TiaMajorVersion < 20)
            {
                throw new PortalException(PortalErrorCode.InvalidState, "ExportBlocksAsDocuments is only supported on TIA Portal V20 or newer");
            }

            PlcBlock[] list;
            try
            {
                list = GetBlocks(softwarePath, regexName).ToArray();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Failed to retrieve block list for {softwarePath}");
                return (exportList, failures);
            }

            for (int i = 0; i < list.Count(); i++)
            {
                var block = list[i];

                _logger?.LogDebug($"- Exporting block as document {i}/{list.Count()} : {block.Name}");

                // Skip inconsistent blocks (TIA generally won’t export them)
                if (!block.IsConsistent)
                {
                    failures.Add($"{block.Name}: skipped - inconsistent (compile first)");
                    _logger?.LogWarning($"Skipping inconsistent block {block.Name}");
                    continue;
                }

                // Determine base directory (preserve group path if requested)
                string targetDir = exportPath;
                if (preservePath && block.Parent is PlcBlockGroup parentGroup)
                {
                    var groupPath = GetPlcBlockGroupPath(parentGroup);
                    if (!string.IsNullOrWhiteSpace(groupPath))
                    {
                        targetDir = Path.Combine(exportPath, SanitizeRelativePath(groupPath));
                    }
                }

                try
                {
                    if (!Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{block.Name}: cannot create directory '{targetDir}' ({ex.Message})");
                    _logger?.LogError(ex, $"Directory creation failed for {targetDir}");
                    continue;
                }

                var fileDcl = Path.Combine(targetDir, $"{block.Name}.s7dcl");
                var fileRes = Path.Combine(targetDir, $"{block.Name}.s7res");

                // Clean previous artifacts
                foreach (var f in new[] { fileDcl, fileRes })
                {
                    try
                    {
                        if (File.Exists(f))
                        {
                            File.Delete(f);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{block.Name}: cannot delete existing '{Path.GetFileName(f)}' ({ex.Message})");
                        _logger?.LogError(ex, $"Failed deleting existing file {f}");
                        // Continue anyway; export might overwrite.
                    }
                }

                try
                {
                    DocumentExportResult? result = null;
                    try
                    {
                        result = block.ExportAsDocuments(new DirectoryInfo(targetDir), block.Name);
                    }
                    catch (EngineeringNotSupportedException ex)
                    {
                        failures.Add($"{block.Name}: document (.s7dcl) export not supported for this block - typically optimized/SCL or system blocks; use the XML ExportBlocks instead ({ex.Message})");
                        _logger?.LogWarning(ex, $"EngineeringNotSupported exporting {block.Name}");
                        continue;
                    }
                    catch (LicenseNotFoundException ex)
                    {
                        failures.Add($"{block.Name}: license not found ({ex.Message})");
                        _logger?.LogError(ex, $"License issue exporting {block.Name}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{block.Name}: export threw ({ex.Message})");
                        _logger?.LogError(ex, $"ExportAsDocuments failed for {block.Name}");
                        continue;
                    }

                    if (result == null)
                    {
                        failures.Add($"{block.Name}: no result returned");
                        continue;
                    }

                    if (result.State == DocumentResultState.Success)
                    {
                        exportList.Add(block);
                    }
                    else
                    {
                        failures.Add($"{block.Name}: result state {result.State}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{block.Name}: unexpected exception ({ex.Message})");
                    _logger?.LogError(ex, $"Unexpected wrapper error for {block.Name}");
                }
            }

            if (failures.Count > 0)
            {
                _logger?.LogWarning($"ExportBlocksAsDocuments completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
            }
            else
            {
                _logger?.LogInformation($"ExportBlocksAsDocuments completed successfully. Exported {exportList.Count} blocks.");
            }

            return (exportList, failures);
        }

        public bool ImportFromDocuments(string softwarePath, string groupPath, string importPath, string fileNameWithoutExtension, ImportDocumentOptions option)
        {
            _logger?.LogInformation($"Importing block from documents: {fileNameWithoutExtension} in {importPath}");

            if (IsProjectNull())
            {
                return false;
            }

            if (Engineering.TiaMajorVersion < 20)
            {
                _logger?.LogWarning("ImportFromDocuments is only supported on TIA Portal V20 or newer");
                return false;
            }

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                    var dir = new DirectoryInfo(importPath);
                    if (!dir.Exists)
                    {
                        _logger?.LogWarning($"Import directory does not exist: {importPath}");
                        return false;
                    }

                    DocumentImportResult? result = null;
                    try
                    {
                        result = (group != null)
                            ? group.Blocks.ImportFromDocuments(dir, fileNameWithoutExtension, option)
                            : plcSoftware.BlockGroup.Blocks.ImportFromDocuments(dir, fileNameWithoutExtension, option);
                    }
                    catch (EngineeringNotSupportedException ex)
                    {
                        throw new PortalException(PortalErrorCode.ExportFailed, $"EngineeringNotSupportedException at file '{fileNameWithoutExtension}'. {ex.Message}", null, ex);
                    }

                    if (result != null && result.State == DocumentResultState.Success)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error importing block from documents");
            }
            return false;
        }

        public IEnumerable<PlcBlock>? ImportBlocksFromDocuments(string softwarePath, string groupPath, string importPath, string regexName, ImportDocumentOptions option, bool preservePath = false)
        {
            _logger?.LogInformation($"Importing blocks from documents in {importPath} with regex '{regexName}'");

            if (IsProjectNull())
            {
                return null;
            }

            if (Engineering.TiaMajorVersion < 20)
            {
                _logger?.LogWarning("ImportBlocksFromDocuments is only supported on TIA Portal V20 or newer");
                return null;
            }

            var imported = new List<PlcBlock>();

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                    var dir = new DirectoryInfo(importPath);
                    if (!dir.Exists)
                    {
                        _logger?.LogWarning($"Import directory does not exist: {importPath}");
                        return imported;
                    }

                    var rx = string.IsNullOrWhiteSpace(regexName)
                        ? null
                        : new Regex(regexName, RegexOptions.Compiled);

                    // Consider .s7dcl as the primary index; .s7res is optional supplemental
                    var files = dir.GetFiles("*.s7dcl", SearchOption.TopDirectoryOnly);
                    foreach (var file in files)
                    {
                        var name = Path.GetFileNameWithoutExtension(file.Name);
                        if (rx != null && !rx.IsMatch(name))
                        {
                            continue;
                        }

                        try
                        {
                            var result = (group != null)
                                ? group.Blocks.ImportFromDocuments(dir, name, option)
                                : plcSoftware.BlockGroup.Blocks.ImportFromDocuments(dir, name, option);

                            if (result != null && result.State == DocumentResultState.Success && result.ImportedPlcBlocks != null)
                            {
                                foreach (var blk in result.ImportedPlcBlocks)
                                {
                                    if (blk != null)
                                    {
                                        imported.Add(blk);
                                    }
                                }
                            }
                        }
                        catch (EngineeringNotSupportedException)
                        {
                            // mixed languages etc.; skip but continue batch
                        }
                        catch (Exception)
                        {
                            // skip problematic item, continue
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error importing blocks from documents");
            }

            return imported;
        }

        #region block crud

        public void DeleteBlock(string softwarePath, string blockPath)
        {
            _logger?.LogInformation($"Deleting block by path: {blockPath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var block = GetBlock(softwarePath, blockPath);

                if (block == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Block '{blockPath}' not found");
                }

                block.Delete();
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to delete block '{blockPath}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath"] = blockPath;
                _logger?.LogError(pex, "DeleteBlock failed for {SoftwarePath} {BlockPath}", softwarePath, blockPath);
                throw pex;
            }
        }

        // Openness has no first-class copy/move between PlcBlockGroups, so both are
        // implemented as export-to-temp-XML + import. The block must be consistent
        // (compiled), because TIA refuses to export inconsistent blocks.

        public PlcBlock? CopyBlock(string softwarePath, string sourceBlockPath, string targetGroupPath, string newName = "")
        {
            _logger?.LogInformation($"Copying block '{sourceBlockPath}' to '{targetGroupPath}'" + (string.IsNullOrEmpty(newName) ? "" : $" as '{newName}'"));

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var block = GetBlock(softwarePath, sourceBlockPath);
                if (block == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Block '{sourceBlockPath}' not found");
                }
                if (!block.IsConsistent)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, $"Block '{block.Name}' is inconsistent (not compiled); compile it first - the copy goes through XML export.");
                }

                var targetGroup = GetPlcBlockGroupByPath(softwarePath, targetGroupPath ?? "");
                if (targetGroup == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Target group '{targetGroupPath}' not found");
                }

                // block names are unique per PLC program, so a same-software copy needs a new name
                var copyName = string.IsNullOrWhiteSpace(newName) ? block.Name + "_Copy" : newName;
                if (GetBlocks(softwarePath).Any(b => b.Name.Equals(copyName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new PortalException(PortalErrorCode.InvalidState, $"A block named '{copyName}' already exists; pass a different newName.");
                }

                var tempDir = Path.Combine(Path.GetTempPath(), "tia_copyblock_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var file = Path.Combine(tempDir, "block.xml");
                    block.Export(new FileInfo(file), ExportOptions.None);

                    // rewrite the block name and let TIA renumber on conflict
                    var doc = XDocument.Load(file);
                    var blockEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks."));
                    var attrList = blockEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                    var nameEl = attrList?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
                    if (nameEl == null)
                    {
                        throw new PortalException(PortalErrorCode.InvalidState, "Could not locate the Name element in the exported block XML");
                    }
                    nameEl.Value = copyName;
                    var autoNumberEl = attrList!.Elements().FirstOrDefault(e => e.Name.LocalName == "AutoNumber");
                    if (autoNumberEl != null)
                    {
                        autoNumberEl.Value = "true";
                    }
                    doc.Save(file);

                    var imported = targetGroup.Blocks.Import(new FileInfo(file), ImportOptions.Override);
                    if (imported == null || imported.Count == 0)
                    {
                        throw new PortalException(PortalErrorCode.InvalidState, "Import of the copied block returned nothing");
                    }

                    return imported[0];
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to copy block '{sourceBlockPath}' to '{targetGroupPath}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["sourceBlockPath"] = sourceBlockPath;
                pex.Data["targetGroupPath"] = targetGroupPath;
                _logger?.LogError(pex, "CopyBlock failed for {SourceBlockPath} -> {TargetGroupPath}", sourceBlockPath, targetGroupPath);
                throw pex;
            }
        }

        public void MoveBlock(string softwarePath, string sourceBlockPath, string targetGroupPath)
        {
            _logger?.LogInformation($"Moving block '{sourceBlockPath}' to '{targetGroupPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var block = GetBlock(softwarePath, sourceBlockPath);
                if (block == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Block '{sourceBlockPath}' not found");
                }
                if (!block.IsConsistent)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, $"Block '{block.Name}' is inconsistent (not compiled); compile it first - the move goes through XML export.");
                }

                var targetGroup = GetPlcBlockGroupByPath(softwarePath, targetGroupPath ?? "");
                if (targetGroup == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Target group '{targetGroupPath}' not found");
                }

                var sourceGroup = block.Parent as PlcBlockGroup;
                if (sourceGroup != null && sourceGroup == targetGroup)
                {
                    return; // already there
                }

                var tempDir = Path.Combine(Path.GetTempPath(), "tia_moveblock_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                var blockName = block.Name;
                try
                {
                    var file = Path.Combine(tempDir, "block.xml");
                    block.Export(new FileInfo(file), ExportOptions.None);

                    // names are program-unique: the original must go before the import
                    block.Delete();

                    try
                    {
                        var imported = targetGroup.Blocks.Import(new FileInfo(file), ImportOptions.Override);
                        if (imported == null || imported.Count == 0)
                        {
                            throw new PortalException(PortalErrorCode.InvalidState, "Import into the target group returned nothing");
                        }
                    }
                    catch (Exception importEx)
                    {
                        // restore the block where it came from rather than losing it
                        try
                        {
                            sourceGroup?.Blocks.Import(new FileInfo(file), ImportOptions.Override);
                            throw new PortalException(PortalErrorCode.InvalidState,
                                $"Import into '{targetGroupPath}' failed ({importEx.Message}); the block was restored to its original group.", null, importEx);
                        }
                        catch (PortalException)
                        {
                            throw;
                        }
                        catch (Exception restoreEx)
                        {
                            throw new PortalException(PortalErrorCode.InvalidState,
                                $"Import into '{targetGroupPath}' failed ({importEx.Message}) AND restoring failed ({restoreEx.Message}). " +
                                $"The block XML is preserved at '{file}' - re-import it manually with ImportBlock.", null, importEx);
                        }
                    }
                }
                finally
                {
                    // keep the temp dir only if the move ended in the unrecoverable branch
                    try
                    {
                        var keep = Directory.Exists(tempDir) &&
                                   GetBlocks(softwarePath).All(b => !b.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase));
                        if (!keep)
                        {
                            Directory.Delete(tempDir, true);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to move block '{sourceBlockPath}' to '{targetGroupPath}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["sourceBlockPath"] = sourceBlockPath;
                pex.Data["targetGroupPath"] = targetGroupPath;
                _logger?.LogError(pex, "MoveBlock failed for {SourceBlockPath} -> {TargetGroupPath}", sourceBlockPath, targetGroupPath);
                throw pex;
            }
        }

        #endregion

        #region type crud

        public void DeleteType(string softwarePath, string typePath)
        {
            _logger?.LogInformation($"Deleting type by path: {typePath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var type = GetType(softwarePath, typePath);

                if (type == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Type '{typePath}' not found");
                }

                type.Delete();
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to delete type '{typePath}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["typePath"] = typePath;
                _logger?.LogError(pex, "DeleteType failed for {SoftwarePath} {TypePath}", softwarePath, typePath);
                throw pex;
            }
        }

        #endregion

        #region block group management

        public PlcBlockGroup? CreateBlockGroup(string softwarePath, string parentGroupPath, string groupName)
        {
            _logger?.LogInformation($"Creating block group '{groupName}' in '{parentGroupPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                if (string.IsNullOrWhiteSpace(groupName))
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Group name cannot be empty");
                }

                var parentGroup = GetPlcBlockGroupByPath(softwarePath, parentGroupPath);

                if (parentGroup == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Parent group '{parentGroupPath}' not found");
                }

                var newGroup = parentGroup.Groups.Create(groupName);
                return newGroup;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to create block group '{groupName}' in '{parentGroupPath}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["parentGroupPath"] = parentGroupPath;
                pex.Data["groupName"] = groupName;
                _logger?.LogError(pex, "CreateBlockGroup failed for {SoftwarePath} {ParentGroupPath}/{GroupName}", softwarePath, parentGroupPath, groupName);
                throw pex;
            }
        }

        public void DeleteBlockGroup(string softwarePath, string groupPath)
        {
            _logger?.LogInformation($"Deleting block group '{groupPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                if (string.IsNullOrWhiteSpace(groupPath))
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Cannot delete the root block group");
                }

                var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                if (group == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Block group '{groupPath}' not found");
                }

                if (group is not PlcBlockUserGroup userGroup)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, $"'{groupPath}' is a system group and cannot be deleted");
                }

                if (group.Blocks.Count > 0 || group.Groups.Count > 0)
                {
                    throw new PortalException(PortalErrorCode.InvalidState,
                        $"Block group '{groupPath}' is not empty ({group.Blocks.Count} block(s), {group.Groups.Count} subgroup(s)); move or delete its contents first.");
                }

                userGroup.Delete();
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to delete block group '{groupPath}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                _logger?.LogError(pex, "DeleteBlockGroup failed for {SoftwarePath} {GroupPath}", softwarePath, groupPath);
                throw pex;
            }
        }

        public (string Name, string Path, int BlockCount, int SubGroupCount) GetBlockGroupInfo(string softwarePath, string groupPath)
        {
            _logger?.LogInformation($"Getting block group info at path: {groupPath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var group = GetPlcBlockGroupByPath(softwarePath, groupPath);

                if (group == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Block group '{groupPath}' not found");
                }

                var blockCount = group.Blocks.Count;
                var subGroupCount = group.Groups.Count;
                var path = GetPlcBlockGroupPath(group);

                return (group.Name, path, blockCount, subGroupCount);
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to get block group info for '{groupPath}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                _logger?.LogError(pex, "GetBlockGroupInfo failed for {SoftwarePath} {GroupPath}", softwarePath, groupPath);
                throw pex;
            }
        }

        #endregion

        #region type group management

        public PlcTypeGroup? CreateTypeGroup(string softwarePath, string parentGroupPath, string groupName)
        {
            _logger?.LogInformation($"Creating type group '{groupName}' in '{parentGroupPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                if (string.IsNullOrWhiteSpace(groupName))
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Group name cannot be empty");
                }

                var parentGroup = GetPlcTypeGroupByPath(softwarePath, parentGroupPath);

                if (parentGroup == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Parent type group '{parentGroupPath}' not found");
                }

                var newGroup = parentGroup.Groups.Create(groupName);
                return newGroup;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to create type group '{groupName}' in '{parentGroupPath}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["parentGroupPath"] = parentGroupPath;
                pex.Data["groupName"] = groupName;
                _logger?.LogError(pex, "CreateTypeGroup failed for {SoftwarePath} {ParentGroupPath}/{GroupName}", softwarePath, parentGroupPath, groupName);
                throw pex;
            }
        }

        public void DeleteTypeGroup(string softwarePath, string groupPath)
        {
            _logger?.LogInformation($"Deleting type group '{groupPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                if (string.IsNullOrWhiteSpace(groupPath))
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Cannot delete the root type group");
                }

                var group = GetPlcTypeGroupByPath(softwarePath, groupPath);
                if (group == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Type group '{groupPath}' not found");
                }

                if (group is not PlcTypeUserGroup userGroup)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, $"'{groupPath}' is a system group and cannot be deleted");
                }

                if (group.Types.Count > 0 || group.Groups.Count > 0)
                {
                    throw new PortalException(PortalErrorCode.InvalidState,
                        $"Type group '{groupPath}' is not empty ({group.Types.Count} type(s), {group.Groups.Count} subgroup(s)); move or delete its contents first.");
                }

                userGroup.Delete();
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to delete type group '{groupPath}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                _logger?.LogError(pex, "DeleteTypeGroup failed for {SoftwarePath} {GroupPath}", softwarePath, groupPath);
                throw pex;
            }
        }

        public (string Name, string Path, int TypeCount, int SubGroupCount) GetTypeGroupInfo(string softwarePath, string groupPath)
        {
            _logger?.LogInformation($"Getting type group info at path: {groupPath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var group = GetPlcTypeGroupByPath(softwarePath, groupPath);

                if (group == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Type group '{groupPath}' not found");
                }

                var typeCount = group.Types.Count;
                var subGroupCount = group.Groups.Count;
                var path = GetPlcTypeGroupPath(group);

                return (group.Name, path, typeCount, subGroupCount);
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to get type group info for '{groupPath}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                _logger?.LogError(pex, "GetTypeGroupInfo failed for {SoftwarePath} {GroupPath}", softwarePath, groupPath);
                throw pex;
            }
        }

        #endregion

        #endregion

        #region tag tables

        public List<PlcTagTable> GetTagTables(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting tag tables...");

            if (IsProjectNull())
            {
                return [];
            }

            var list = new List<PlcTagTable>();

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var tagTables = plcSoftware.TagTableGroup?.TagTables;

                    if (tagTables != null)
                    {
                        bool isRegex = !string.IsNullOrEmpty(regexName) && regexName.Any(c => _regexChars.Contains(c));

                        foreach (PlcTagTable table in tagTables)
                        {
                            if (string.IsNullOrEmpty(regexName))
                            {
                                list.Add(table);
                            }
                            else if (isRegex)
                            {
                                if (Regex.IsMatch(table.Name, regexName, RegexOptions.IgnoreCase))
                                {
                                    list.Add(table);
                                }
                            }
                            else
                            {
                                if (table.Name.Equals(regexName, StringComparison.OrdinalIgnoreCase))
                                {
                                    list.Add(table);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting tag tables");
            }

            return list;
        }

        public PlcTagTable? GetTagTable(string softwarePath, string tagTableName)
        {
            _logger?.LogInformation($"Getting tag table: {tagTableName}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var tagTables = plcSoftware.TagTableGroup?.TagTables;

                    if (tagTables != null)
                    {
                        foreach (PlcTagTable table in tagTables)
                        {
                            if (table.Name.Equals(tagTableName, StringComparison.OrdinalIgnoreCase))
                            {
                                return table;
                            }
                        }
                    }
                }

                var candidates = GetTagTables(softwarePath).Select(t => t.Name).ToList();
                throw new PortalException(PortalErrorCode.NotFound, $"Tag table '{tagTableName}' not found", candidates);
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Failed to get tag table", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["tagTableName"] = tagTableName;
                _logger?.LogError(pex, "GetTagTable failed for {SoftwarePath} {TagTableName}", softwarePath, tagTableName);
                throw pex;
            }
        }

        public List<PlcTag> GetTags(string softwarePath, string tagTableName, string regexName = "")
        {
            _logger?.LogInformation($"Getting tags from table: {tagTableName}");

            var list = new List<PlcTag>();

            try
            {
                var table = GetTagTable(softwarePath, tagTableName);

                if (table != null)
                {
                    bool isRegex = !string.IsNullOrEmpty(regexName) && regexName.Any(c => _regexChars.Contains(c));

                    foreach (PlcTag tag in table.Tags)
                    {
                        if (string.IsNullOrEmpty(regexName))
                        {
                            list.Add(tag);
                        }
                        else if (isRegex)
                        {
                            if (Regex.IsMatch(tag.Name, regexName, RegexOptions.IgnoreCase))
                            {
                                list.Add(tag);
                            }
                        }
                        else
                        {
                            if (tag.Name.Equals(regexName, StringComparison.OrdinalIgnoreCase))
                            {
                                list.Add(tag);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Failed to get tags", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["tagTableName"] = tagTableName;
                _logger?.LogError(pex, "GetTags failed for {SoftwarePath} {TagTableName}", softwarePath, tagTableName);
                throw pex;
            }

            return list;
        }

        public PlcTagTable? ExportTagTable(string softwarePath, string tagTableName, string exportPath)
        {
            _logger?.LogInformation($"Exporting tag table: {tagTableName}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var table = GetTagTable(softwarePath, tagTableName);

                if (table == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Tag table '{tagTableName}' not found");
                }

                exportPath = Path.Combine(exportPath, $"{table.Name}.xml");

                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }

                table.Export(new FileInfo(exportPath), ExportOptions.WithDefaults);

                return table;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export tag table failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["tagTableName"] = tagTableName;
                pex.Data["exportPath"] = exportPath;
                _logger?.LogError(pex, "ExportTagTable failed for {SoftwarePath} {TagTableName} -> {ExportPath}", softwarePath, tagTableName, exportPath);
                throw pex;
            }
        }

        public bool ImportTagTable(string softwarePath, string importPath)
        {
            _logger?.LogInformation($"Importing tag table from path: {importPath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var tagTableGroup = plcSoftware.TagTableGroup;

                    if (tagTableGroup != null)
                    {
                        var fileInfo = new FileInfo(importPath);
                        if (fileInfo.Exists)
                        {
                            var imported = tagTableGroup.TagTables.Import(fileInfo, ImportOptions.Override);
                            if (imported != null && imported.Count > 0)
                            {
                                return true;
                            }
                        }
                        else
                        {
                            throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Import tag table failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportTagTable failed for {SoftwarePath} {ImportPath}", softwarePath, importPath);
                throw pex;
            }
        }

        public PlcTag? CreateTag(string softwarePath, string tagTableName, string tagName, string dataType, string logicalAddress, string comment = "")
        {
            _logger?.LogInformation($"Creating tag '{tagName}' in table '{tagTableName}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var table = GetTagTable(softwarePath, tagTableName);

                if (table == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Tag table '{tagTableName}' not found");
                }

                var tag = table.Tags.Create(tagName, dataType, logicalAddress);

                if (tag != null && !string.IsNullOrEmpty(comment))
                {
                    tag.Comment.Items[0].Text = comment;
                }

                return tag;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Create tag failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["tagTableName"] = tagTableName;
                pex.Data["tagName"] = tagName;
                _logger?.LogError(pex, "CreateTag failed for {SoftwarePath} {TagTableName} {TagName}", softwarePath, tagTableName, tagName);
                throw pex;
            }
        }

        public bool DeleteTag(string softwarePath, string tagTableName, string tagName)
        {
            _logger?.LogInformation($"Deleting tag '{tagName}' from table '{tagTableName}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var table = GetTagTable(softwarePath, tagTableName);

                if (table == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Tag table '{tagTableName}' not found");
                }

                foreach (PlcTag tag in table.Tags)
                {
                    if (tag.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                    {
                        tag.Delete();
                        return true;
                    }
                }

                var candidates = table.Tags.Cast<PlcTag>().Select(t => t.Name).ToList();
                throw new PortalException(PortalErrorCode.NotFound, $"Tag '{tagName}' not found in table '{tagTableName}'", candidates);
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Delete tag failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["tagTableName"] = tagTableName;
                pex.Data["tagName"] = tagName;
                _logger?.LogError(pex, "DeleteTag failed for {SoftwarePath} {TagTableName} {TagName}", softwarePath, tagTableName, tagName);
                throw pex;
            }
        }

        private PlcTag FindTagInTable(string softwarePath, string tagTableName, string tagName)
        {
            var table = GetTagTable(softwarePath, tagTableName);
            if (table == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"Tag table '{tagTableName}' not found");
            }

            var tag = table.Tags.Cast<PlcTag>().FirstOrDefault(t => t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase));
            if (tag == null)
            {
                var candidates = table.Tags.Cast<PlcTag>().Select(t => t.Name).ToList();
                throw new PortalException(PortalErrorCode.NotFound, $"Tag '{tagName}' not found in table '{tagTableName}'", candidates);
            }

            return tag;
        }

        /// <summary>
        /// Renames a tag in place, keeping its address/data type/comment. MUTATES the project.
        /// </summary>
        public PlcTag RenameTag(string softwarePath, string tagTableName, string tagName, string newName)
        {
            _logger?.LogInformation($"Renaming tag '{tagName}' to '{newName}' in table '{tagTableName}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                if (string.IsNullOrWhiteSpace(newName))
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "newName cannot be empty");
                }

                var tag = FindTagInTable(softwarePath, tagTableName, tagName);
                tag.SetAttribute("Name", newName);
                return tag;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to rename tag '{tagName}' to '{newName}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["tagTableName"] = tagTableName;
                pex.Data["tagName"] = tagName;
                pex.Data["newName"] = newName;
                _logger?.LogError(pex, "RenameTag failed for {TagName} -> {NewName}", tagName, newName);
                throw pex;
            }
        }

        /// <summary>
        /// Updates a tag's comment (and optionally address/data type). MUTATES the project.
        /// </summary>
        public PlcTag UpdateTag(string softwarePath, string tagTableName, string tagName, string? comment = null, string? logicalAddress = null, string? dataType = null)
        {
            _logger?.LogInformation($"Updating tag '{tagName}' in table '{tagTableName}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var tag = FindTagInTable(softwarePath, tagTableName, tagName);

                if (logicalAddress != null)
                {
                    tag.SetAttribute("LogicalAddress", logicalAddress);
                }
                if (dataType != null)
                {
                    tag.SetAttribute("DataTypeName", dataType);
                }
                if (comment != null)
                {
                    tag.Comment.Items[0].Text = comment;
                }

                return tag;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to update tag '{tagName}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["tagTableName"] = tagTableName;
                pex.Data["tagName"] = tagName;
                _logger?.LogError(pex, "UpdateTag failed for {TagName}", tagName);
                throw pex;
            }
        }

        /// <summary>
        /// Creates many tags in one call (one table). Continues past per-tag failures and
        /// reports each result. MUTATES the project.
        /// </summary>
        public List<(string Name, bool Success, string Error)> BulkCreateTags(
            string softwarePath, string tagTableName,
            List<(string Name, string DataType, string LogicalAddress, string Comment)> tags)
        {
            _logger?.LogInformation($"Bulk-creating {tags.Count} tags in table '{tagTableName}'");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var table = GetTagTable(softwarePath, tagTableName);
            if (table == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"Tag table '{tagTableName}' not found");
            }

            var results = new List<(string Name, bool Success, string Error)>();
            foreach (var t in tags)
            {
                try
                {
                    var tag = RunWithTimeout(() => table.Tags.Create(t.Name, t.DataType, t.LogicalAddress), 30, $"CreateTag '{t.Name}'");
                    if (tag != null && !string.IsNullOrEmpty(t.Comment))
                    {
                        tag.Comment.Items[0].Text = t.Comment;
                    }
                    results.Add((t.Name, true, ""));
                }
                catch (Exception ex)
                {
                    results.Add((t.Name, false, ex.Message));
                }
            }

            return results;
        }

        /// <summary>
        /// Renames a block or UDT in place via SetAttribute("Name"). MUTATES the project.
        /// </summary>
        public string RenameBlockOrType(string softwarePath, string objectPath, string newName, bool isType)
        {
            _logger?.LogInformation($"Renaming {(isType ? "type" : "block")} '{objectPath}' to '{newName}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                if (string.IsNullOrWhiteSpace(newName))
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "newName cannot be empty");
                }

                IEngineeringObject? obj = isType
                    ? GetType(softwarePath, objectPath)
                    : (IEngineeringObject?)GetBlock(softwarePath, objectPath);

                if (obj == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"{(isType ? "Type" : "Block")} '{objectPath}' not found");
                }

                var oldName = obj.GetAttribute("Name")?.ToString() ?? objectPath;
                obj.SetAttribute("Name", newName);
                return oldName;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams,
                    $"Failed to rename '{objectPath}' to '{newName}': {ex.Message}. " +
                    "Note: know-how-protected or inconsistent objects, and names referenced by other blocks, may refuse the rename.", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["objectPath"] = objectPath;
                pex.Data["newName"] = newName;
                _logger?.LogError(pex, "Rename failed for {ObjectPath} -> {NewName}", objectPath, newName);
                throw pex;
            }
        }

        #endregion

        #region watch/force tables

        public List<PlcWatchTable> GetWatchTables(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting watch tables...");

            if (IsProjectNull())
            {
                return [];
            }

            var list = new List<PlcWatchTable>();

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var watchTableGroup = plcSoftware.WatchAndForceTableGroup;

                    if (watchTableGroup != null)
                    {
                        bool isRegex = !string.IsNullOrEmpty(regexName) && regexName.Any(c => _regexChars.Contains(c));

                        foreach (PlcWatchTable table in watchTableGroup.WatchTables)
                        {
                            if (string.IsNullOrEmpty(regexName))
                            {
                                list.Add(table);
                            }
                            else if (isRegex)
                            {
                                if (Regex.IsMatch(table.Name, regexName, RegexOptions.IgnoreCase))
                                {
                                    list.Add(table);
                                }
                            }
                            else
                            {
                                if (table.Name.Equals(regexName, StringComparison.OrdinalIgnoreCase))
                                {
                                    list.Add(table);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting watch tables");
            }

            return list;
        }

        public PlcWatchTable? ExportWatchTable(string softwarePath, string watchTableName, string exportPath)
        {
            _logger?.LogInformation($"Exporting watch table: {watchTableName}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var watchTableGroup = plcSoftware.WatchAndForceTableGroup;

                    if (watchTableGroup != null)
                    {
                        PlcWatchTable? foundTable = null;

                        foreach (PlcWatchTable table in watchTableGroup.WatchTables)
                        {
                            if (table.Name.Equals(watchTableName, StringComparison.OrdinalIgnoreCase))
                            {
                                foundTable = table;
                                break;
                            }
                        }

                        if (foundTable == null)
                        {
                            var candidates = new List<string>();
                            foreach (PlcWatchTable t in watchTableGroup.WatchTables)
                            {
                                candidates.Add(t.Name);
                            }
                            throw new PortalException(PortalErrorCode.NotFound, $"Watch table '{watchTableName}' not found", candidates);
                        }

                        exportPath = Path.Combine(exportPath, $"{foundTable.Name}.xml");

                        if (File.Exists(exportPath))
                        {
                            File.Delete(exportPath);
                        }

                        foundTable.Export(new FileInfo(exportPath), ExportOptions.WithDefaults);

                        return foundTable;
                    }
                }

                throw new PortalException(PortalErrorCode.InvalidState, "Could not access watch table group");
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export watch table failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["watchTableName"] = watchTableName;
                pex.Data["exportPath"] = exportPath;
                _logger?.LogError(pex, "ExportWatchTable failed for {SoftwarePath} {WatchTableName} -> {ExportPath}", softwarePath, watchTableName, exportPath);
                throw pex;
            }
        }

        public bool ImportWatchTable(string softwarePath, string importPath)
        {
            _logger?.LogInformation($"Importing watch table from path: {importPath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var watchTableGroup = plcSoftware.WatchAndForceTableGroup;

                    if (watchTableGroup != null)
                    {
                        var fileInfo = new FileInfo(importPath);
                        if (fileInfo.Exists)
                        {
                            var imported = watchTableGroup.WatchTables.Import(fileInfo, ImportOptions.Override);
                            if (imported != null && imported.Count > 0)
                            {
                                return true;
                            }
                        }
                        else
                        {
                            throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Import watch table failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportWatchTable failed for {SoftwarePath} {ImportPath}", softwarePath, importPath);
                throw pex;
            }
        }

        #endregion

        #region private helper

        // Re-attaches to a RUNNING TIA Portal process and re-acquires the open
        // project/session. Never starts a new TIA instance (unlike ConnectPortal).
        // Used to transparently recover from disposed handles after the user
        // restarted TIA, closed/re-opened the project, or after SaveAs.
        private bool TryReattach()
        {
            try
            {
                _project = null;
                _session = null;
                _portal = null;

                var processes = TiaPortal.GetProcesses();
                if (!processes.Any())
                {
                    return false;
                }

                _portal = processes.First().Attach();

                if (_portal.LocalSessions.Any())
                {
                    _session = _portal.LocalSessions.First();
                    _project = _session.Project;
                }
                else if (_portal.Projects.Any())
                {
                    _project = _portal.Projects.First();
                }

                _logger?.LogInformation("Re-attached to running TIA Portal (project: {Project})", _project?.Name ?? "-");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Re-attach to TIA Portal failed");
                _portal = null;
                _project = null;
                _session = null;
                return false;
            }
        }

        private bool IsPortalHealthy()
        {
            if (_portal == null)
            {
                return false;
            }

            try
            {
                // touching a disposed/orphaned portal object throws
                var _ = _portal.GetCurrentProcess();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool IsProjectHealthy()
        {
            if (_project == null)
            {
                return false;
            }

            try
            {
                // reading an attribute of a closed project throws
                // 'Access to a disposed object ... is not possible'
                var _ = _project.Name;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool IsPortalNull()
        {
            if (!IsPortalHealthy() && !TryReattach())
            {
                _logger?.LogWarning("No TIA portal available.");

                return true;
            }

            return false;
        }

        private bool IsProjectNull()
        {
            if (IsProjectHealthy())
            {
                return false;
            }

            // stale or missing handle: re-acquire from the (possibly re-attached) portal
            if (!IsPortalHealthy())
            {
                TryReattach();
            }
            else
            {
                try
                {
                    _session = _portal!.LocalSessions.FirstOrDefault();
                    _project = _session != null ? _session.Project : _portal.Projects.FirstOrDefault();
                }
                catch (Exception)
                {
                    TryReattach();
                }

                // A cached attach can go stale in a subtler way: the portal handle still
                // answers (GetCurrentProcess works) but its Projects collection reads
                // empty while the project IS open - observed after failed imports. A
                // fresh attach to the same process sees the project again.
                if (!IsProjectHealthy())
                {
                    TryReattach();
                }
            }

            if (!IsProjectHealthy())
            {
                _logger?.LogWarning("No TIA project available.");

                return true;
            }

            return false;
        }

        private bool IsSessionNull()
        {
            if (_session == null)
            {
                _logger?.LogWarning("No TIA session available.");

                return true;
            }

            return false;
        }

        #region  GetTree ...

        private string GetTreePrefix(List<bool> ancestorStates, bool isLast)
        {
            var prefix = new StringBuilder();
            
            // Build prefix based on ancestor states
            for (int i = 0; i < ancestorStates.Count; i++)
            {
                prefix.Append(ancestorStates[i] ? "    " : "│   ");
            }
            
            // Add current level connector
            prefix.Append(isLast ? "└── " : "├── ");
            return prefix.ToString();
        }

        private void GetProjectTreeDevices(StringBuilder sb, DeviceComposition devices, List<bool> ancestorStates)
        {
            if (devices.Count == 0) return;
            
            // Check if this is the last main section
            var hasOtherSections = (_project?.DeviceGroups != null && _project.DeviceGroups.Count > 0) ||
                                  (_project?.UngroupedDevicesGroup != null);
            var isLastMainSection = !hasOtherSections;
            
            sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastMainSection)}Devices [Collection]");

            var deviceList = devices.ToList();
            var newAncestorStates = new List<bool>(ancestorStates) { isLastMainSection };
            
            for (int i = 0; i < deviceList.Count; i++)
            {
                var device = deviceList[i];
                var isLastDevice = i == deviceList.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastDevice)}{device.Name} [Device: {device.TypeIdentifier}]");

                if (device.DeviceItems != null && device.DeviceItems.Count > 0)
                {
                    GetProjectTreeDeviceItemsRecursive(sb, device.DeviceItems, new List<bool>(newAncestorStates) { isLastDevice });
                }
            }
        }

        private void GetProjectTreeGroups(StringBuilder sb, DeviceUserGroupComposition groups, List<bool> ancestorStates)
        {
            if (groups.Count == 0) return;
            
            var isLastMainSection = _project?.UngroupedDevicesGroup == null;
            
            sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastMainSection)}Groups [Collection]");

            var groupList = groups.ToList();
            var newAncestorStates = new List<bool>(ancestorStates) { isLastMainSection };
            
            for (int i = 0; i < groupList.Count; i++)
            {
                var group = groupList[i];
                var isLastGroup = i == groupList.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastGroup)}{group.Name} [Group]");

                var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup };
                
                if (group.Devices != null && group.Devices.Count > 0)
                {
                    GetProjectTreeGroupDevices(sb, group.Devices, groupAncestorStates, group.Groups != null && group.Groups.Count > 0);
                }
                
                if (group.Groups != null && group.Groups.Count > 0)
                {
                    GetProjectTreeSubGroups(sb, group.Groups, groupAncestorStates);
                }
            }
        }
        
        private void GetProjectTreeGroupDevices(StringBuilder sb, DeviceComposition devices, List<bool> ancestorStates, bool hasSubGroups)
        {
            var deviceList = devices.ToList();
            
            for (int i = 0; i < deviceList.Count; i++)
            {
                var device = deviceList[i];
                var isLastDevice = i == deviceList.Count - 1 && !hasSubGroups;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastDevice)}{device.Name} [Device]");
                
                if (device.DeviceItems != null && device.DeviceItems.Count > 0)
                {
                    GetProjectTreeDeviceItemsRecursive(sb, device.DeviceItems, new List<bool>(ancestorStates) { isLastDevice });
                }
            }
        }
        
        private void GetProjectTreeSubGroups(StringBuilder sb, DeviceUserGroupComposition groups, List<bool> ancestorStates)
        {
            var groupList = groups.ToList();
            
            for (int i = 0; i < groupList.Count; i++)
            {
                var group = groupList[i];
                var isLastGroup = i == groupList.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastGroup)}{group.Name} [Subgroup]");
                
                var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup };
                
                if (group.Devices != null && group.Devices.Count > 0)
                {
                    GetProjectTreeGroupDevices(sb, group.Devices, groupAncestorStates, group.Groups != null && group.Groups.Count > 0);
                }
                
                if (group.Groups != null && group.Groups.Count > 0)
                {
                    GetProjectTreeSubGroups(sb, group.Groups, groupAncestorStates);
                }
            }
        }

        private void GetProjectTreeDeviceItemsRecursive(StringBuilder sb, DeviceItemComposition deviceItems, List<bool> ancestorStates)
        {
            var deviceItemsList = deviceItems.ToList();
            
            for (int i = 0; i < deviceItemsList.Count; i++)
            {
                var deviceItem = deviceItemsList[i];
                var isLastDeviceItem = i == deviceItemsList.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastDeviceItem)}{deviceItem.Name} [DeviceItem]");
                
                var itemAncestorStates = new List<bool>(ancestorStates) { isLastDeviceItem };
                
                // Get software first
                GetProjectTreeDeviceItemSoftware(sb, deviceItem, itemAncestorStates);
                
                // Then get items
                if (deviceItem.Items != null && deviceItem.Items.Count > 0)
                {
                    GetProjectTreeItems(sb, deviceItem.Items, itemAncestorStates, deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0);
                }
                
                // Finally get sub-device items
                if (deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0)
                {
                    GetProjectTreeDeviceItemsRecursive(sb, deviceItem.DeviceItems, itemAncestorStates);
                }
            }
        }
        
        private void GetProjectTreeItems(StringBuilder sb, DeviceItemAssociation items, List<bool> ancestorStates, bool hasSubDeviceItems)
        {
            var itemsList = items.ToList();
            
            for (int i = 0; i < itemsList.Count; i++)
            {
                var subItem = itemsList[i];
                var isLastItem = i == itemsList.Count - 1 && !hasSubDeviceItems;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastItem)}{subItem.Name} [Hardware Component]");
            }
        }


        private void GetProjectTreeDeviceItemSoftware(StringBuilder sb, DeviceItem deviceItem, List<bool> ancestorStates)
        {
            var softwareContainer = deviceItem.GetService<SoftwareContainer>();
            var hasSoftware = false;
            
            //PLC software
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var hasOtherItems = (deviceItem.Items != null && deviceItem.Items.Count > 0) ||
                                   (deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0);
                sb.AppendLine($"{GetTreePrefix(ancestorStates, !hasOtherItems)}PlcSoftware: {plcSoftware.Name} [PLC Program]");
                hasSoftware = true;
            }

            //WinCC HMI software
            if (softwareContainer?.Software is HmiTarget hmiTarget)
            {
                var hasOtherItems = (deviceItem.Items != null && deviceItem.Items.Count > 0) ||
                                   (deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0);
                sb.AppendLine($"{GetTreePrefix(ancestorStates, !hasOtherItems && !hasSoftware)}HmiTarget: {hmiTarget.Name} [HMI Program]");
            }

            //Unified HMI software: dlls will only exist on TIA Portal V19 and newer.
            if (Engineering.TiaMajorVersion >= 19)
                TryGetUnifiedSoftware(sb, deviceItem, ancestorStates, softwareContainer, hasSoftware);
        }

        private bool TryGetUnifiedSoftware(StringBuilder sb, DeviceItem deviceItem, List<bool> ancestorStates, SoftwareContainer? softwareContainer, bool hasSoftware)
        {
            if (softwareContainer?.Software is HmiSoftware hmiSoftware)
            {
                var hasOtherItems = (deviceItem.Items != null && deviceItem.Items.Count > 0) ||
                                    (deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0);
                sb.AppendLine($"{GetTreePrefix(ancestorStates, !hasOtherItems && !hasSoftware)}HmiSoftware: {hmiSoftware.Name} [HMI Program]");
                hasSoftware = true;
            }

            return hasSoftware;
        }

        private void GetProjectTreeUngroupedDeviceGroup(StringBuilder sb, DeviceSystemGroup ungroupedDevicesGroup, List<bool> ancestorStates)
        {
            sb.AppendLine($"{GetTreePrefix(ancestorStates, true)}UngroupedDevicesGroup: {ungroupedDevicesGroup.Name} [System Group]");

            if (ungroupedDevicesGroup.Devices != null && ungroupedDevicesGroup.Devices.Count > 0)
            {
                var deviceList = ungroupedDevicesGroup.Devices.ToList();
                var newAncestorStates = new List<bool>(ancestorStates) { true };
                
                for (int i = 0; i < deviceList.Count; i++)
                {
                    var device = deviceList[i];
                    var isLastDevice = i == deviceList.Count - 1;
                    
                    sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastDevice)}{device.Name} [{device.TypeIdentifier}]");
                }
            }
        }

        #endregion

        #region GetSoftwareTree ...

        public string GetSoftwareTree(string softwarePath)
        {
            _logger?.LogInformation("Getting software tree for path: {SoftwarePath}", softwarePath);

            if (IsProjectNull())
            {
                return string.Empty;
            }

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    StringBuilder sb = new();
                    sb.AppendLine($"{plcSoftware.Name} [PLC Software]");
                    
                    var ancestorStates = new List<bool>();
                    var sections = new List<Action>();
                    
                    var hasBlocks = plcSoftware.BlockGroup != null;
                    var hasTypes = plcSoftware.TypeGroup != null;
                    
                    // Add blocks section
                    if (hasBlocks)
                    {
                        var blockGroup = plcSoftware.BlockGroup;
                        if (blockGroup != null)
                        {
                            sections.Add(() => GetSoftwareTreeBlockGroup(sb, blockGroup, ancestorStates, "Program blocks", !hasTypes));
                        }
                    }
                    
                    // Add types section
                    if (hasTypes)
                    {
                        var typeGroup = plcSoftware.TypeGroup;
                        if (typeGroup != null)
                        {
                            sections.Add(() => GetSoftwareTreeTypeGroup(sb, typeGroup, ancestorStates, "PLC data types", true));
                        }
                    }
                    
                    
                    // Execute sections
                    for (int i = 0; i < sections.Count; i++)
                    {
                        sections[i]();
                    }

                    return sb.ToString();
                }
                else
                {
                    return $"No PLC software found at path: {softwarePath}";
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting software tree for {SoftwarePath}", softwarePath);
                return $"Error retrieving software tree: {ex.Message}";
            }
        }
        
        private void GetSoftwareTreeBlockGroup(StringBuilder sb, PlcBlockGroup blockGroup, List<bool> ancestorStates, string groupLabel, bool isLastSection)
        {
            sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastSection)}{groupLabel}"); // [Collection]
            var newAncestorStates = new List<bool>(ancestorStates) { isLastSection };
            
            // Get blocks in this group
            var blocks = blockGroup.Blocks.ToList();
            var subGroups = blockGroup.Groups.ToList();
            
            // First, add all blocks
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                // Block is last only if it's the last block AND there are no subgroups following
                var isLastBlock = (i == blocks.Count - 1) && (subGroups.Count == 0);

                var blockTypeName = new[] { "ArrayDB", "GlobalDB", "InstanceDB" }.Contains(block.GetType().Name)
                    ? "DB"
                    : block.GetType().Name;

                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastBlock)}{block.Name} [{blockTypeName}{block.Number}, {block.ProgrammingLanguage}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastGroup)}{subGroup.Name}"); // [Block Group]

                var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup };
                GetSoftwareTreeBlockGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }
        
        private void GetSoftwareTreeBlockGroupRecursive(StringBuilder sb, PlcBlockGroup blockGroup, List<bool> ancestorStates)
        {
            // Get blocks in this group
            var blocks = blockGroup.Blocks.ToList();
            var subGroups = blockGroup.Groups.ToList();
            
            // First, add all blocks
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                // Block is last only if it's the last block AND there are no subgroups following
                var isLastBlock = (i == blocks.Count - 1) && (subGroups.Count == 0);

                var blockTypeName = new[] { "ArrayDB", "GlobalDB", "InstanceDB" }.Contains(block.GetType().Name)
                    ? "DB"
                    : block.GetType().Name;

                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastBlock)}{block.Name} [{blockTypeName}{block.Number}, {block.ProgrammingLanguage}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastGroup)}{subGroup.Name}"); // [Block Group]

                var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup };
                GetSoftwareTreeBlockGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }
        
        private void GetSoftwareTreeTypeGroup(StringBuilder sb, PlcTypeGroup typeGroup, List<bool> ancestorStates, string groupLabel, bool isLastSection)
        {
            
            sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastSection)}{groupLabel}"); // [Collection]
            var newAncestorStates = new List<bool>(ancestorStates) { isLastSection };
            
            // Get types in this group
            var types = typeGroup.Types.ToList();
            var subGroups = typeGroup.Groups.ToList();
            
            // First, add all types
            for (int i = 0; i < types.Count; i++)
            {
                var type = types[i];
                // Type is last only if it's the last type AND there are no subgroups following
                var isLastType = (i == types.Count - 1) && (subGroups.Count == 0);

                var typeTypeName = type.GetType().Name;
                typeTypeName = typeTypeName=="PlcStruct" ? "UDT": typeTypeName;

                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastType)}{type.Name} [{typeTypeName}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastGroup)}{subGroup.Name}"); // [Type Group]

                var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup };
                GetSoftwareTreeTypeGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }
        
        private void GetSoftwareTreeTypeGroupRecursive(StringBuilder sb, PlcTypeGroup typeGroup, List<bool> ancestorStates)
        {
            // Get types in this group
            var types = typeGroup.Types.ToList();
            var subGroups = typeGroup.Groups.ToList();
            
            // First, add all types
            for (int i = 0; i < types.Count; i++)
            {
                var type = types[i];
                // Type is last only if it's the last type AND there are no subgroups following
                var isLastType = (i == types.Count - 1) && (subGroups.Count == 0);

                var typeTypeName = type.GetType().Name;
                typeTypeName = typeTypeName == "PlcStruct" ? "UDT" : typeTypeName;

                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastType)}{type.Name} [{typeTypeName}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastGroup)}{subGroup.Name}"); // [Type Group]

                var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup };
                GetSoftwareTreeTypeGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }

        #endregion

        #region GetSoftwareContainer ...

        private SoftwareContainer? GetSoftwareContainer(string softwarePath)
        {
            if (_project == null)
            {
                return null;
            }

            string[] pathSegments = softwarePath.Split('/');
            int index = 0;

            if (index >= pathSegments.Length)
                return null;

            // in Devices (top-level and ungrouped)
            foreach (var devices in RootDeviceCompositions())
            {
                var softwareContainer = GetSoftwareContainerInDevices(devices, pathSegments, index);
                if (softwareContainer != null)
                {
                    return softwareContainer;
                }
            }

            // in Groups
            if (_project.DeviceGroups != null)
            {
                var softwareContainer = GetSoftwareContainerInGroups(_project.DeviceGroups, pathSegments, index);
                if (softwareContainer != null)
                {
                    return softwareContainer;
                }
            }

            return null;
        }

        private SoftwareContainer? GetSoftwareContainerInDevices(DeviceComposition devices, string[] pathSegments, int index)
        {
            if (devices == null || index >= pathSegments.Length)
                return null;

            string segment = pathSegments[index];

            foreach (Device device in devices)
            {
                // a pc based plc has a Device.Name = 'PC-System_1' or something like that, which is visible in the TIA-Portal IDE
                // use segment to find device, then the following segments for the device item chain
                if (device.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                {
                    var item = FindDeviceItemInItems(device.DeviceItems, pathSegments, index + 1);
                    var softwareContainer = item?.GetService<SoftwareContainer>();
                    if (softwareContainer != null)
                    {
                        return softwareContainer;
                    }
                }

                // a hardware plc has a Device.Name = 'S7-1500/ET200MP-Station_1' or something like that, which is not visible in the TIA-Portal IDE
                // segment names a device item directly - scoped to this one device
                var direct = FindDeviceItemInItems(device.DeviceItems, pathSegments, index);
                var directContainer = direct?.GetService<SoftwareContainer>();
                if (directContainer != null)
                {
                    return directContainer;
                }
            }

            return null;
        }

        private SoftwareContainer? GetSoftwareContainerInGroups(DeviceUserGroupComposition groups, string[] pathSegments, int index)
        {
            if (index >= pathSegments.Length)
                return null;

            string segment = pathSegments[index];
            SoftwareContainer? softwareContainer = null;

            if (groups != null)
            {
                var group = groups.FirstOrDefault(g => g.Name.Equals(segment));
                if (group != null)
                {
                    // when segment matched
                    softwareContainer = GetSoftwareContainerInDevices(group.Devices, pathSegments, index + 1);
                    if (softwareContainer != null)
                    {
                        return softwareContainer;
                    }

                    return GetSoftwareContainerInGroups(group.Groups, pathSegments, index + 1);
                }
            }

            return null;
        }

        #endregion

        #region Get...ByPath

        // Device compositions a path may start in: top-level project devices and the
        // ungrouped-devices system group (where GSD/PROFINET field devices land).
        private IEnumerable<DeviceComposition> RootDeviceCompositions()
        {
            if (_project?.Devices != null)
            {
                yield return _project.Devices;
            }

            if (_project?.UngroupedDevicesGroup?.Devices != null)
            {
                yield return _project.UngroupedDevicesGroup.Devices;
            }
        }

        private Device? GetDeviceByPath(string devicePath)
        {
            if (_project == null || string.IsNullOrWhiteSpace(devicePath))
                return null;

            var pathSegments = devicePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (pathSegments.Length == 0)
            {
                return null;
            }

            // Plain device name: top-level devices and ungrouped (GSD) devices
            if (pathSegments.Length == 1)
            {
                foreach (var devices in RootDeviceCompositions())
                {
                    var found = devices.FirstOrDefault(d => d.Name.Equals(pathSegments[0], StringComparison.OrdinalIgnoreCase));
                    if (found != null)
                    {
                        return found;
                    }
                }

                return null;
            }

            // Group-prefixed path: walk the user group chain, last segment is the device
            return FindDeviceInGroups(_project.DeviceGroups, pathSegments, 0);
        }

        private Device? FindDeviceInGroups(DeviceUserGroupComposition? groups, string[] segments, int index)
        {
            if (groups == null || index >= segments.Length)
            {
                return null;
            }

            var group = groups.FirstOrDefault(g => g.Name.Equals(segments[index], StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                return null;
            }

            if (index == segments.Length - 2)
            {
                var device = group.Devices.FirstOrDefault(d => d.Name.Equals(segments[index + 1], StringComparison.OrdinalIgnoreCase));
                if (device != null)
                {
                    return device;
                }
            }

            return FindDeviceInGroups(group.Groups, segments, index + 1);
        }

        private DeviceItem? GetDeviceItemByPath(string deviceItemPath)
        {
            if (_project == null || string.IsNullOrWhiteSpace(deviceItemPath))
            {
                return null;
            }

            var pathSegments = deviceItemPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (pathSegments.Length == 0)
            {
                return null;
            }

            // Top-level and ungrouped devices
            foreach (var devices in RootDeviceCompositions())
            {
                var found = FindDeviceItemInDevices(devices, pathSegments, 0);
                if (found != null)
                {
                    return found;
                }
            }

            // Group-prefixed paths
            return FindDeviceItemInGroups(_project.DeviceGroups, pathSegments, 0);
        }

        private DeviceItem? FindDeviceItemInGroups(DeviceUserGroupComposition? groups, string[] segments, int index)
        {
            if (groups == null || index >= segments.Length)
            {
                return null;
            }

            var group = groups.FirstOrDefault(g => g.Name.Equals(segments[index], StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                return null;
            }

            var found = FindDeviceItemInDevices(group.Devices, segments, index + 1);
            if (found != null)
            {
                return found;
            }

            return FindDeviceItemInGroups(group.Groups, segments, index + 1);
        }

        // Resolves a device item path against one device composition. Matching is scoped:
        // each path segment must name the device / a device item at that exact level
        // (no global fall-through to identically-named items of other devices, which
        // used to return the wrong device's rack for GSD paths).
        private static DeviceItem? FindDeviceItemInDevices(DeviceComposition? devices, string[] segments, int index)
        {
            if (devices == null || index >= segments.Length)
            {
                return null;
            }

            foreach (Device device in devices)
            {
                // path includes the device name
                if (device.Name.Equals(segments[index], StringComparison.OrdinalIgnoreCase))
                {
                    var found = FindDeviceItemInItems(device.DeviceItems, segments, index + 1);
                    if (found != null)
                    {
                        return found;
                    }
                }

                // station name omitted (a hardware station's Device.Name like
                // 'S7-1500/ET200MP-Station_1' is not visible in the IDE): the segment
                // names a device item directly - but still scoped to this one device
                var direct = FindDeviceItemInItems(device.DeviceItems, segments, index);
                if (direct != null)
                {
                    return direct;
                }
            }

            return null;
        }

        private static DeviceItem? FindDeviceItemInItems(DeviceItemComposition? items, string[] segments, int index)
        {
            if (items == null || index >= segments.Length)
            {
                return null;
            }

            foreach (DeviceItem item in items)
            {
                if (!item.Name.Equals(segments[index], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (index == segments.Length - 1)
                {
                    return item;
                }

                var found = FindDeviceItemInItems(item.DeviceItems, segments, index + 1);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private PlcBlockGroup? GetPlcBlockGroupByPath(string softwarePath, string groupPath)
        {
            if (_project == null)
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                if (plcSoftware?.BlockGroup == null)
                {
                    return null;
                }


                // Split the path by '/' to get each group name
                var groupNames = groupPath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);

                PlcBlockGroup? currentGroup = plcSoftware.BlockGroup;

                foreach (var groupName in groupNames)
                {
                    currentGroup = currentGroup.Groups.FirstOrDefault(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));

                    if (currentGroup == null)
                    {
                        return null;
                    }
                }

                return currentGroup;
            }

            return null;
        }

        private PlcTypeGroup? GetPlcTypeGroupByPath(string softwarePath, string groupPath)
        {
            if (_project == null)
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                if (plcSoftware?.TypeGroup == null)
                {
                    return null;
                }

                var groupNames = groupPath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);

                PlcTypeGroup? currentGroup = plcSoftware.TypeGroup;

                foreach (var groupName in groupNames)
                {
                    currentGroup = currentGroup.Groups.FirstOrDefault(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));

                    if (currentGroup == null)
                    {
                        return null;
                    }
                }

                return currentGroup;
            }

            return null;
        }

        private string GetPlcBlockGroupPath(PlcBlockGroup group)
        {
            if (group == null)
            {
                return string.Empty;
            }

            PlcBlockGroup? nullableGroup = group;
            var path = group.Name;

            while (nullableGroup != null && nullableGroup.Parent != null)
            {
                try
                {
                    //group = (PlcBlockGroup) group.Parent;
                    if (group is PlcBlockSystemGroup systemGroup)
                    {
                        // do not get parent for system group
                        break;
                    }

                    nullableGroup = nullableGroup.Parent as PlcBlockGroup;
                }
                catch (Exception)
                {
                    // Handle any exceptions that may occur while accessing the parent
                    break;
                }

                if (nullableGroup != null)
                {
                    path = $"{nullableGroup.Name}/{path}";
                }
            }

            return path;
        }

        private string GetPlcTypeGroupPath(PlcTypeGroup group)
        {
            if (group == null)
            {
                return string.Empty;
            }

            PlcTypeGroup? nullableGroup = group;
            var path = group.Name;

            while (nullableGroup != null && nullableGroup.Parent != null)
            {
                try
                {
                    //group = (PlcTypeGroup) group.Parent;
                    if (group is PlcTypeSystemGroup systemGroup)
                    {
                        // do not get parent for system group
                        break;
                    }

                    nullableGroup = nullableGroup.Parent as PlcTypeGroup;
                }
                catch (Exception)
                {
                    // Handle any exceptions that may occur while accessing the parent
                    break;
                }

                if (nullableGroup != null)
                {
                    path = $"{nullableGroup.Name}/{path}";
                }
            }

            return path;
        }

        #endregion

        #region GetRecursive ...

        private bool GetDevicesRecursive(DeviceUserGroup group, List<Device> list, string regexName = "")
        {
            var anySuccess = false;

            foreach (var composition in group.Devices)
            {
                if (composition is Device device)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(device.Name, regexName, RegexOptions.IgnoreCase))
                        {
                            continue; // Skip this device if it doesn't match the pattern
                        }
                    }
                    catch (Exception)
                    {
                        // Invalid regex pattern, skip this device
                        continue;
                    }

                    list.Add(device);

                    anySuccess = true;
                }
            }

            foreach (var subgroup in group.Groups)
            {
                anySuccess = GetDevicesRecursive(subgroup, list, regexName);
            }

            return anySuccess;
        }

        private bool GetBlocksRecursive(PlcBlockGroup group, List<PlcBlock> list, string regexName = "")
        {
            var anySuccess = false;

            foreach (var composition in group.Blocks)
            {
                if (composition is PlcBlock block)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(block.Name, regexName, RegexOptions.IgnoreCase))
                        {
                            continue; // Skip this block if it doesn't match the pattern
                        }
                    }
                    catch (Exception)
                    {
                        // Invalid regex pattern, skip this block
                        continue;
                    }

                    list.Add(block);

                    anySuccess = true;
                }
            }

            foreach (var subgroup in group.Groups)
            {
                anySuccess = GetBlocksRecursive(subgroup, list, regexName);
            }

            return anySuccess;
        }

        private bool GetTypesRecursive(PlcTypeGroup group, List<PlcType> list, string regexName = "")
        {
            var anySuccess = false;

            foreach (var composition in group.Types)
            {
                if (composition is PlcType type)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(type.Name, regexName, RegexOptions.IgnoreCase))
                        {
                            continue; // Skip this block if it doesn't match the pattern
                        }
                    }
                    catch (Exception)
                    {
                        // Invalid regex pattern, skip this block
                        continue;
                    }

                    list.Add(type);

                    anySuccess = true;
                }

            }

            foreach (PlcTypeGroup subgroup in group.Groups)
            {
                anySuccess = GetTypesRecursive(subgroup, list, regexName);
            }

            return anySuccess;
        }

        #endregion

        #endregion

        #region external sources

        public List<PlcExternalSource> GetExternalSources(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting external sources...");

            if (IsProjectNull())
            {
                return [];
            }

            var list = new List<PlcExternalSource>();

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var sourceGroup = plcSoftware.ExternalSourceGroup;
                    if (sourceGroup != null)
                    {
                        foreach (var source in sourceGroup.ExternalSources)
                        {
                            if (string.IsNullOrEmpty(regexName))
                            {
                                list.Add(source);
                            }
                            else
                            {
                                try
                                {
                                    if (regexName.IndexOfAny(_regexChars) >= 0)
                                    {
                                        var regex = new Regex(regexName, RegexOptions.IgnoreCase);
                                        if (regex.IsMatch(source.Name))
                                        {
                                            list.Add(source);
                                        }
                                    }
                                    else
                                    {
                                        if (source.Name.Equals(regexName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            list.Add(source);
                                        }
                                    }
                                }
                                catch (Exception)
                                {
                                    // Invalid regex, skip
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Error getting external sources
            }

            return list;
        }

        public bool ImportExternalSource(string softwarePath, string groupPath, string importPath)
        {
            _logger?.LogInformation($"Importing external source from path: {importPath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var sourceGroup = plcSoftware.ExternalSourceGroup;
                    if (sourceGroup == null)
                    {
                        throw new PortalException(PortalErrorCode.NotFound, "External source group not found");
                    }

                    var fileInfo = new FileInfo(importPath);
                    if (!fileInfo.Exists)
                    {
                        throw new PortalException(PortalErrorCode.InvalidParams, $"File not found: {importPath}");
                    }

                    var name = Path.GetFileName(importPath);
                    sourceGroup.ExternalSources.CreateFromFile(name, importPath);
                    return true;
                }

                throw new PortalException(PortalErrorCode.NotFound, "PLC software not found");
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Import external source failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportExternalSource failed for {SoftwarePath} {ImportPath}", softwarePath, importPath);
                throw pex;
            }
        }

        public bool GenerateBlocksFromSource(string softwarePath, string sourceName)
        {
            _logger?.LogInformation($"Generating blocks from external source: {sourceName}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var sourceGroup = plcSoftware.ExternalSourceGroup;
                    if (sourceGroup == null)
                    {
                        throw new PortalException(PortalErrorCode.NotFound, "External source group not found");
                    }

                    var source = sourceGroup.ExternalSources
                        .FirstOrDefault(s => s.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));

                    if (source == null)
                    {
                        var candidates = sourceGroup.ExternalSources
                            .Select(s => s.Name)
                            .ToList();
                        throw new PortalException(PortalErrorCode.NotFound, $"External source '{sourceName}' not found", candidates);
                    }

                    source.GenerateBlocksFromSource();
                    return true;
                }

                throw new PortalException(PortalErrorCode.NotFound, "PLC software not found");
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Generate blocks from source failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["sourceName"] = sourceName;
                _logger?.LogError(pex, "GenerateBlocksFromSource failed for {SoftwarePath} {SourceName}", softwarePath, sourceName);
                throw pex;
            }
        }

        public bool DeleteExternalSource(string softwarePath, string sourceName)
        {
            _logger?.LogInformation($"Deleting external source: {sourceName}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var sourceGroup = plcSoftware.ExternalSourceGroup;
                    if (sourceGroup == null)
                    {
                        throw new PortalException(PortalErrorCode.NotFound, "External source group not found");
                    }

                    var source = sourceGroup.ExternalSources
                        .FirstOrDefault(s => s.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));

                    if (source == null)
                    {
                        var candidates = sourceGroup.ExternalSources
                            .Select(s => s.Name)
                            .ToList();
                        throw new PortalException(PortalErrorCode.NotFound, $"External source '{sourceName}' not found", candidates);
                    }

                    source.Delete();
                    return true;
                }

                throw new PortalException(PortalErrorCode.NotFound, "PLC software not found");
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Delete external source failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["sourceName"] = sourceName;
                _logger?.LogError(pex, "DeleteExternalSource failed for {SoftwarePath} {SourceName}", softwarePath, sourceName);
                throw pex;
            }
        }

        public bool ExportExternalSource(string softwarePath, string sourceName, string exportPath)
        {
            // PlcExternalSource has no Export/read-back in the Openness API (V20) -
            // an external source is a write-only staging object for GenerateBlocksFromSource.
            throw new PortalException(PortalErrorCode.InvalidState,
                "TIA Openness does not expose external source content (PlcExternalSource has no Export). " +
                "To get a block's source text, use GetBlockCode / ExportBlock instead; " +
                "to re-generate sources from blocks, use GenerateBlocksFromSource's inverse via GetBlockCode.");
        }

        #endregion

        #region cross-references

        public List<(string SourceObject, string ReferencedObject, string ReferenceType, string Path)> GetCrossReferences(string softwarePath, string objectPath)
        {
            _logger?.LogInformation($"Getting cross-references: {objectPath}");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            // Resolve the target object (block first, then type).
            IEngineeringServiceProvider provider = GetBlock(softwarePath, objectPath) as IEngineeringServiceProvider
                                                   ?? GetType(softwarePath, objectPath) as IEngineeringServiceProvider;
            if (provider == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"Block or type not found at '{objectPath}'");
            }

            var results = new List<(string, string, string, string)>();
            try
            {
                var service = provider.GetService<CrossReferenceService>();
                if (service == null)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Cross-reference service is not available for this object");
                }

                var crResult = service.GetCrossReferences(CrossReferenceFilter.AllObjects);
                if (crResult?.Sources != null)
                {
                    foreach (var src in crResult.Sources)
                    {
                        var srcName = src.Name ?? "";
                        foreach (var reference in src.References)
                        {
                            var refName = reference.Name ?? "";
                            var refPath = reference.Path ?? "";
                            var locations = reference.Locations;
                            if (locations != null && locations.Any())
                            {
                                foreach (var loc in locations)
                                {
                                    results.Add((srcName, refName, loc.ReferenceType.ToString(), refPath));
                                }
                            }
                            else
                            {
                                results.Add((srcName, refName, "", refPath));
                            }
                        }
                    }
                }
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.InvalidState, $"Failed to read cross-references for '{objectPath}': {ex.Message}", null, ex);
            }

            return results;
        }

        #endregion


        #region online access

        public bool GoOnline(string deviceItemPath)
        {
            _logger?.LogInformation($"Going online for device item: {deviceItemPath}");

            if (IsProjectNull())
            {
                return false;
            }

            try
            {
                var deviceItem = GetDeviceItemByPath(deviceItemPath);
                if (deviceItem == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Device item not found: {deviceItemPath}");
                }

                var onlineProvider = deviceItem.GetService<OnlineProvider>();
                if (onlineProvider == null)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, $"OnlineProvider not available for device item: {deviceItemPath}");
                }

                onlineProvider.GoOnline();

                return onlineProvider.State == OnlineState.Online;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidState, "GoOnline failed", null, ex);
                pex.Data["deviceItemPath"] = deviceItemPath;
                _logger?.LogError(pex, "GoOnline failed for {DeviceItemPath}", deviceItemPath);
                throw pex;
            }
        }

        public bool GoOffline(string deviceItemPath)
        {
            _logger?.LogInformation($"Going offline for device item: {deviceItemPath}");

            if (IsProjectNull())
            {
                return false;
            }

            try
            {
                var deviceItem = GetDeviceItemByPath(deviceItemPath);
                if (deviceItem == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Device item not found: {deviceItemPath}");
                }

                var onlineProvider = deviceItem.GetService<OnlineProvider>();
                if (onlineProvider == null)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, $"OnlineProvider not available for device item: {deviceItemPath}");
                }

                onlineProvider.GoOffline();

                return onlineProvider.State == OnlineState.Offline;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidState, "GoOffline failed", null, ex);
                pex.Data["deviceItemPath"] = deviceItemPath;
                _logger?.LogError(pex, "GoOffline failed for {DeviceItemPath}", deviceItemPath);
                throw pex;
            }
        }

        public string DownloadToDevice(string deviceItemPath)
        {
            throw new PortalException(PortalErrorCode.InvalidState, "Download requires TIA Portal UI. Use Online -> Download in TIA Portal directly.");
        }

        public string UploadFromDevice(string deviceItemPath)
        {
            throw new PortalException(PortalErrorCode.InvalidState, "Upload requires TIA Portal UI. Use Online -> Upload in TIA Portal directly.");
        }

        #endregion

        #region compare

        public List<(string ObjectPath, string ChangeType, string Details)> CompareOfflineOnline(string softwarePath)
        {
            _logger?.LogInformation($"Comparing offline/online for: {softwarePath}");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"No PLC software found at path '{softwarePath}'");
            }

            try
            {
                var result = plcSoftware.CompareToOnline();
                var list = new List<(string ObjectPath, string ChangeType, string Details)>();
                if (result?.RootElement != null)
                {
                    CollectCompareElements(result.RootElement, "", list);
                }
                return list;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    $"Offline/online compare failed: {ex.Message}. The PLC must be reachable - GoOnline first.", null, ex);
            }
        }

        private void CollectCompareElements(global::Siemens.Engineering.Compare.CompareResultElement element, string parentPath, List<(string ObjectPath, string ChangeType, string Details)> list)
        {
            var name = element.LeftName;
            if (string.IsNullOrEmpty(name))
            {
                name = element.RightName;
            }
            var path = string.IsNullOrEmpty(parentPath) ? name ?? "" : $"{parentPath}/{name}";

            var state = "";
            try { state = element.ComparisonResult.ToString(); } catch { }

            // only report differences; identical sub-trees stay out of the result
            if (!string.Equals(state, "Identical", StringComparison.OrdinalIgnoreCase))
            {
                var details = "";
                try { details = element.DetailedInformation ?? ""; } catch { }
                list.Add((path, state, details));
            }

            if (element.Elements != null)
            {
                foreach (global::Siemens.Engineering.Compare.CompareResultElement sub in element.Elements)
                {
                    CollectCompareElements(sub, path, list);
                }
            }
        }

        public List<(string Property, string Value1, string Value2)> CompareBlocks(string softwarePath, string blockPath1, string blockPath2)
        {
            _logger?.LogInformation($"Comparing blocks: {blockPath1} vs {blockPath2}");

            if (IsProjectNull())
            {
                return new List<(string, string, string)>();
            }

            try
            {
                var block1 = GetBlock(softwarePath, blockPath1);
                var block2 = GetBlock(softwarePath, blockPath2);

                if (block1 == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Block not found: {blockPath1}");
                }
                if (block2 == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Block not found: {blockPath2}");
                }

                var differences = new List<(string Property, string Value1, string Value2)>();

                // Compare key attributes
                var attributeNames = new[] { "Name", "Number", "ProgrammingLanguage", "MemoryLayout", "IsConsistent", "HeaderFamily", "HeaderVersion" };
                foreach (var attrName in attributeNames)
                {
                    try
                    {
                        var val1 = block1.GetAttribute(attrName)?.ToString() ?? "";
                        var val2 = block2.GetAttribute(attrName)?.ToString() ?? "";
                        if (!val1.Equals(val2, StringComparison.Ordinal))
                        {
                            differences.Add((attrName, val1, val2));
                        }
                    }
                    catch
                    {
                        // Attribute may not exist on all block types
                    }
                }

                // Compare modification dates
                var mod1 = block1.GetAttribute("ModifiedDate")?.ToString() ?? "";
                var mod2 = block2.GetAttribute("ModifiedDate")?.ToString() ?? "";
                if (!mod1.Equals(mod2, StringComparison.Ordinal))
                {
                    differences.Add(("ModifiedDate", mod1, mod2));
                }

                return differences;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Compare blocks failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath1"] = blockPath1;
                pex.Data["blockPath2"] = blockPath2;
                _logger?.LogError(pex, "CompareBlocks failed for {BlockPath1} vs {BlockPath2}", blockPath1, blockPath2);
                throw pex;
            }
        }

        #endregion

        #region library management

        public (List<(string Name, string Path)> MasterCopies, List<(string Name, string Version)> Types) GetProjectLibrary(string regexName = "")
        {
            _logger?.LogInformation($"Getting project library contents with filter: {regexName}");

            if (IsProjectNull())
            {
                return (new List<(string, string)>(), new List<(string, string)>());
            }

            try
            {
                var project = _project as Project;
                if (project == null)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Project is not a local project (may be a session)");
                }

                var library = project.ProjectLibrary;
                var masterCopies = new List<(string Name, string Path)>();
                var types = new List<(string Name, string Version)>();

                Regex? regex = null;
                if (!string.IsNullOrEmpty(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                // Enumerate master copies
                CollectMasterCopies(library.MasterCopyFolder, masterCopies, regex, "");

                // Enumerate library types
                CollectLibraryTypes(library.TypeFolder, types, regex, "");

                return (masterCopies, types);
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "GetProjectLibrary failed", null, ex);
                pex.Data["regexName"] = regexName;
                _logger?.LogError(pex, "GetProjectLibrary failed with filter {RegexName}", regexName);
                throw pex;
            }
        }

        public List<(string Name, string Path)> GetGlobalLibraries(string regexName = "")
        {
            _logger?.LogInformation($"Getting global libraries with filter: {regexName}");

            if (IsPortalNull())
            {
                return new List<(string, string)>();
            }

            try
            {
                var libraries = new List<(string Name, string Path)>();

                Regex? regex = null;
                if (!string.IsNullOrEmpty(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var library in _portal!.GlobalLibraries)
                {
                    if (regex == null || regex.IsMatch(library.Name))
                    {
                        libraries.Add((library.Name, library.Path?.FullName ?? ""));
                    }
                }

                return libraries;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "GetGlobalLibraries failed", null, ex);
                pex.Data["regexName"] = regexName;
                _logger?.LogError(pex, "GetGlobalLibraries failed with filter {RegexName}", regexName);
                throw pex;
            }
        }

        public bool OpenGlobalLibrary(string libraryPath)
        {
            _logger?.LogInformation($"Opening global library: {libraryPath}");

            if (IsPortalNull())
            {
                return false;
            }

            try
            {
                var fileInfo = new FileInfo(libraryPath);
                if (!fileInfo.Exists)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Library file not found: {libraryPath}");
                }

                _portal!.GlobalLibraries.Open(fileInfo, OpenMode.ReadWrite);

                return true;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "OpenGlobalLibrary failed", null, ex);
                pex.Data["libraryPath"] = libraryPath;
                _logger?.LogError(pex, "OpenGlobalLibrary failed for {LibraryPath}", libraryPath);
                throw pex;
            }
        }

        public bool CopyToLibrary(string softwarePath, string blockPath, string libraryFolder = "")
        {
            _logger?.LogInformation($"Copying block '{blockPath}' to project library" + (string.IsNullOrEmpty(libraryFolder) ? "" : $" folder '{libraryFolder}'"));

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var block = GetBlock(softwarePath, blockPath);
                if (block == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Block '{blockPath}' not found");
                }
                if (!block.IsConsistent)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, $"Block '{block.Name}' is inconsistent (not compiled); compile it before copying to the library.");
                }

                var project = _project as Project;
                var library = project?.ProjectLibrary;
                if (library == null)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project library available");
                }

                var folder = (MasterCopyFolder)library.MasterCopyFolder;
                if (!string.IsNullOrWhiteSpace(libraryFolder))
                {
                    foreach (var segment in libraryFolder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var sub = folder.Folders.FirstOrDefault(f => f.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                                  ?? folder.Folders.Create(segment);
                        folder = sub;
                    }
                }

                // PlcBlock does not statically declare IMasterCopySource; Openness objects
                // are remoting proxies whose interface support is resolved at runtime.
                var source = (IMasterCopySource)(object)block;
                var masterCopy = RunWithTimeout(() => folder.MasterCopies.Create(source), 60, "CopyToLibrary");
                return masterCopy != null;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.InvalidParams, $"Failed to copy block '{blockPath}' to the project library", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath"] = blockPath;
                pex.Data["libraryFolder"] = libraryFolder;
                _logger?.LogError(pex, "CopyToLibrary failed for {BlockPath}", blockPath);
                throw pex;
            }
        }

        public bool CopyFromLibrary(string softwarePath, string masterCopyName, string targetGroupPath)
        {
            _logger?.LogInformation($"Copying from library: {masterCopyName} to {targetGroupPath}");

            if (IsProjectNull())
            {
                return false;
            }

            try
            {
                var project = _project as Project;
                if (project == null)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Project is not a local project (may be a session)");
                }

                var masterCopy = FindMasterCopy(project.ProjectLibrary.MasterCopyFolder, masterCopyName);
                if (masterCopy == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"Master copy not found: {masterCopyName}");
                }

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var targetGroup = GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
                    if (targetGroup == null)
                    {
                        targetGroup = plcSoftware.BlockGroup;
                    }

                    targetGroup.Blocks.CreateFrom(masterCopy);

                    return true;
                }

                throw new PortalException(PortalErrorCode.NotFound, $"PLC software not found at path: {softwarePath}");
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "CopyFromLibrary failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["masterCopyName"] = masterCopyName;
                pex.Data["targetGroupPath"] = targetGroupPath;
                _logger?.LogError(pex, "CopyFromLibrary failed for {MasterCopyName}", masterCopyName);
                throw pex;
            }
        }

        public List<(string Name, string Version, string Path)> GetLibraryTypes(string libraryName = "")
        {
            _logger?.LogInformation($"Getting library types from: {libraryName}");

            if (IsProjectNull())
            {
                return new List<(string, string, string)>();
            }

            try
            {
                var types = new List<(string Name, string Version, string Path)>();

                if (string.IsNullOrEmpty(libraryName))
                {
                    // Get from project library
                    var project = _project as Project;
                    if (project == null)
                    {
                        throw new PortalException(PortalErrorCode.InvalidState, "Project is not a local project (may be a session)");
                    }

                    CollectLibraryTypeVersions(project.ProjectLibrary.TypeFolder, types, "");
                }
                else
                {
                    // Get from global library by name
                    if (IsPortalNull())
                    {
                        return types;
                    }

                    var globalLib = _portal!.GlobalLibraries.FirstOrDefault(l => l.Name.Equals(libraryName, StringComparison.OrdinalIgnoreCase));
                    if (globalLib == null)
                    {
                        throw new PortalException(PortalErrorCode.NotFound, $"Global library not found: {libraryName}");
                    }

                    CollectLibraryTypeVersions(globalLib.TypeFolder, types, "");
                }

                return types;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "GetLibraryTypes failed", null, ex);
                pex.Data["libraryName"] = libraryName;
                _logger?.LogError(pex, "GetLibraryTypes failed for {LibraryName}", libraryName);
                throw pex;
            }
        }

        #endregion

        #region project creation

        public bool CreateProject(string projectPath, string projectName)
        {
            _logger?.LogInformation($"Creating project: {projectName} at {projectPath}");

            if (IsPortalNull())
            {
                return false;
            }

            try
            {
                if (_project != null)
                {
                    (_project as Project)?.Close();
                    _project = null;
                }

                if (_session != null)
                {
                    _session.Close();
                    _session = null;
                }

                var directoryInfo = new DirectoryInfo(projectPath);
                if (!directoryInfo.Exists)
                {
                    directoryInfo.Create();
                }

                _project = _portal!.Projects.Create(directoryInfo, projectName);

                return _project != null;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "CreateProject failed", null, ex);
                pex.Data["projectPath"] = projectPath;
                pex.Data["projectName"] = projectName;
                _logger?.LogError(pex, "CreateProject failed for {ProjectName} at {ProjectPath}", projectName, projectPath);
                throw pex;
            }
        }

        #endregion

        #region multi-user

        public (bool IsMultiuser, string? ServerName, List<string> Users) GetMultiuserInfo()
        {
            _logger?.LogInformation("Getting multiuser info...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var isMultiuser = (_session != null) || (_project is MultiuserProject);

            // V20 Openness exposes almost nothing on MultiuserProject - probe the
            // common attributes and degrade gracefully.
            string? serverName = null;
            foreach (var attr in new[] { "ServerName", "ServerProjectName", "Server" })
            {
                try
                {
                    serverName = (_project as IEngineeringObject)?.GetAttribute(attr)?.ToString();
                    if (!string.IsNullOrEmpty(serverName))
                    {
                        break;
                    }
                }
                catch (Exception)
                {
                }
            }

            // connected users are not exposed by the Openness API
            return (isMultiuser, serverName, new List<string>());
        }

        #endregion

        #region library helpers

        private void CollectMasterCopies(MasterCopyFolder folder, List<(string Name, string Path)> list, Regex? regex, string path)
        {
            foreach (var masterCopy in folder.MasterCopies)
            {
                var fullPath = string.IsNullOrEmpty(path) ? masterCopy.Name : $"{path}/{masterCopy.Name}";
                if (regex == null || regex.IsMatch(masterCopy.Name))
                {
                    list.Add((masterCopy.Name, fullPath));
                }
            }

            foreach (var subFolder in folder.Folders)
            {
                var subPath = string.IsNullOrEmpty(path) ? subFolder.Name : $"{path}/{subFolder.Name}";
                CollectMasterCopies(subFolder, list, regex, subPath);
            }
        }

        private void CollectLibraryTypes(LibraryTypeFolder folder, List<(string Name, string Version)> list, Regex? regex, string path)
        {
            foreach (var libraryType in folder.Types)
            {
                if (regex == null || regex.IsMatch(libraryType.Name))
                {
                    var versions = libraryType.Versions;
                    var latestVersion = versions.LastOrDefault();
                    list.Add((libraryType.Name, latestVersion?.VersionNumber.ToString() ?? ""));
                }
            }

            foreach (var subFolder in folder.Folders)
            {
                CollectLibraryTypes(subFolder, list, regex, $"{path}/{subFolder.Name}");
            }
        }

        private void CollectLibraryTypeVersions(LibraryTypeFolder folder, List<(string Name, string Version, string Path)> list, string path)
        {
            foreach (var libraryType in folder.Types)
            {
                foreach (var version in libraryType.Versions)
                {
                    var fullPath = string.IsNullOrEmpty(path) ? libraryType.Name : $"{path}/{libraryType.Name}";
                    list.Add((libraryType.Name, version.VersionNumber.ToString(), fullPath));
                }
            }

            foreach (var subFolder in folder.Folders)
            {
                var subPath = string.IsNullOrEmpty(path) ? subFolder.Name : $"{path}/{subFolder.Name}";
                CollectLibraryTypeVersions(subFolder, list, subPath);
            }
        }

        private MasterCopyFolder GetOrCreateMasterCopyFolder(MasterCopyFolder root, string folderPath)
        {
            var segments = folderPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var current = root;

            foreach (var segment in segments)
            {
                var found = current.Folders.FirstOrDefault(f => f.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
                if (found != null)
                {
                    current = found;
                }
                else
                {
                    current = current.Folders.Create(segment);
                }
            }

            return current;
        }

        private MasterCopy? FindMasterCopy(MasterCopyFolder folder, string name)
        {
            var found = folder.MasterCopies.FirstOrDefault(mc => mc.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                return found;
            }

            foreach (var subFolder in folder.Folders)
            {
                found = FindMasterCopy(subFolder, name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        #endregion


        #region HMI

        public HmiTarget? GetHmiTarget(string softwarePath)
        {
            var container = GetSoftwareContainer(softwarePath);
            return container?.Software as HmiTarget;
        }

        public HmiSoftware? GetHmiSoftware(string softwarePath)
        {
            var container = GetSoftwareContainer(softwarePath);
            return container?.Software as HmiSoftware;
        }

        #region HMI Tags

        public List<object> GetHmiTagTables(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting HMI tag tables...");

            if (IsProjectNull())
            {
                return [];
            }

            var hmi = GetHmiTarget(softwarePath);
            if (hmi == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
            }

            var list = new List<object>();
            CollectHmiTagTables(hmi.TagFolder.TagTables, hmi.TagFolder.Folders, list, regexName);
            return list;
        }

        private void CollectHmiTagTables(
            global::Siemens.Engineering.Hmi.Tag.TagTableComposition tables,
            global::Siemens.Engineering.Hmi.Tag.TagUserFolderComposition folders,
            List<object> list,
            string regexName)
        {
            if (tables != null)
            {
                foreach (global::Siemens.Engineering.Hmi.Tag.TagTable table in tables)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(table.Name, regexName, RegexOptions.IgnoreCase))
                        {
                            continue;
                        }
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    list.Add(table);
                }
            }

            if (folders != null)
            {
                foreach (global::Siemens.Engineering.Hmi.Tag.TagUserFolder folder in folders)
                {
                    CollectHmiTagTables(folder.TagTables, folder.Folders, list, regexName);
                }
            }
        }

        public List<object> GetHmiTags(string softwarePath, string tagTableName, string regexName = "")
        {
            _logger?.LogInformation("Getting HMI tags...");

            if (IsProjectNull())
            {
                return [];
            }

            var hmi = GetHmiTarget(softwarePath);
            if (hmi == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
            }

            var tables = new List<object>();
            CollectHmiTagTables(hmi.TagFolder.TagTables, hmi.TagFolder.Folders, tables, "");

            var table = tables
                .OfType<global::Siemens.Engineering.Hmi.Tag.TagTable>()
                .FirstOrDefault(t => t.Name.Equals(tagTableName, StringComparison.OrdinalIgnoreCase));

            if (table == null)
            {
                var names = tables.OfType<global::Siemens.Engineering.Hmi.Tag.TagTable>().Select(t => t.Name);
                throw new PortalException(PortalErrorCode.NotFound, $"HMI tag table '{tagTableName}' not found", names);
            }

            var list = new List<object>();
            foreach (global::Siemens.Engineering.Hmi.Tag.Tag tag in table.Tags)
            {
                try
                {
                    if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(tag.Name, regexName, RegexOptions.IgnoreCase))
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    continue;
                }

                list.Add(tag);
            }

            return list;
        }

        private global::Siemens.Engineering.Hmi.Tag.TagTable? FindHmiTagTable(HmiTarget hmi, string tagTableName, out List<string> available)
        {
            var tables = new List<object>();
            CollectHmiTagTables(hmi.TagFolder.TagTables, hmi.TagFolder.Folders, tables, "");
            available = tables.OfType<global::Siemens.Engineering.Hmi.Tag.TagTable>().Select(t => t.Name).ToList();
            return tables
                .OfType<global::Siemens.Engineering.Hmi.Tag.TagTable>()
                .FirstOrDefault(t => t.Name.Equals(tagTableName, StringComparison.OrdinalIgnoreCase));
        }

        // Resolves an export target: a path ending in .xml is used as the file,
        // anything else is treated as a directory (created if needed) and the
        // sanitized object name becomes the filename. Deletes a pre-existing file,
        // because Openness refuses to overwrite on export.
        private static string ResolveExportFile(string exportPath, string objectName)
        {
            string file;
            if (exportPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                file = exportPath;
                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            else
            {
                if (!Directory.Exists(exportPath))
                {
                    Directory.CreateDirectory(exportPath);
                }
                file = Path.Combine(exportPath, SanitizeFileName(objectName) + ".xml");
            }

            if (File.Exists(file))
            {
                File.Delete(file);
            }

            return file;
        }

        public string ExportHmiTagTable(string softwarePath, string tagTableName, string exportPath)
        {
            _logger?.LogInformation($"Exporting HMI tag table '{tagTableName}' to '{exportPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var hmi = GetHmiTarget(softwarePath);
                if (hmi == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
                }

                var table = FindHmiTagTable(hmi, tagTableName, out var available);
                if (table == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"HMI tag table '{tagTableName}' not found", available);
                }

                var file = ResolveExportFile(exportPath, table.Name);
                table.Export(new FileInfo(file), ExportOptions.WithDefaults);
                return file;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, $"Failed to export HMI tag table '{tagTableName}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["tagTableName"] = tagTableName;
                pex.Data["exportPath"] = exportPath;
                _logger?.LogError(pex, "ExportHmiTagTable failed for {SoftwarePath} {TagTableName} -> {ExportPath}", softwarePath, tagTableName, exportPath);
                throw pex;
            }
        }

        public List<string> ImportHmiTagTable(string softwarePath, string importPath)
        {
            _logger?.LogInformation($"Importing HMI tag table from '{importPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var hmi = GetHmiTarget(softwarePath);
                if (hmi == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
                }

                var fileInfo = new FileInfo(importPath);
                if (!fileInfo.Exists)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                }

                var imported = RunWithTimeout(
                    () => hmi.TagFolder.TagTables.Import(fileInfo, ImportOptions.Override),
                    60, "ImportHmiTagTable");

                if (imported == null || imported.Count == 0)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Import returned no tag tables (check the SimaticML content)");
                }

                return imported.Select(t => t.Name).ToList();
            }
            catch (Exception ex)
            {
                var message = $"Failed to import HMI tag table from '{importPath}': {DescribeException(ex)}";

                // The most common bound-tag failure: the file references a connection that
                // is not an integrated one, so symbolic ControllerTag resolution aborts.
                try
                {
                    if (ex is not PortalException && File.Exists(importPath))
                    {
                        var content = File.ReadAllText(importPath);
                        if (content.Contains("<ControllerTag"))
                        {
                            message += " HINT: this file contains PLC-bound (symbolic) tags - they only import when the HMI has an " +
                                       "INTEGRATED connection to the PLC (created by dragging an HMI connection in 'Devices & networks'); " +
                                       "a manually-added connection with just an IP cannot resolve symbols, and the connection name in the " +
                                       "XML must match the integrated connection's name.";
                        }
                    }
                }
                catch (Exception)
                {
                }

                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, message, null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportHmiTagTable failed for {SoftwarePath} from {ImportPath}", softwarePath, importPath);
                throw pex;
            }
        }

        #endregion

        #region HMI Screens

        public List<object> GetScreens(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting HMI screens...");

            if (IsProjectNull())
            {
                return [];
            }

            var hmi = GetHmiTarget(softwarePath);
            if (hmi == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
            }

            var list = new List<object>();
            CollectScreens(hmi.ScreenFolder.Screens, hmi.ScreenFolder.Folders, list, regexName);
            return list;
        }

        private void CollectScreens(
            global::Siemens.Engineering.Hmi.Screen.ScreenComposition screens,
            global::Siemens.Engineering.Hmi.Screen.ScreenUserFolderComposition folders,
            List<object> list,
            string regexName)
        {
            if (screens != null)
            {
                foreach (global::Siemens.Engineering.Hmi.Screen.Screen screen in screens)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(screen.Name, regexName, RegexOptions.IgnoreCase))
                        {
                            continue;
                        }
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    list.Add(screen);
                }
            }

            if (folders != null)
            {
                foreach (global::Siemens.Engineering.Hmi.Screen.ScreenUserFolder folder in folders)
                {
                    CollectScreens(folder.Screens, folder.Folders, list, regexName);
                }
            }
        }

        private global::Siemens.Engineering.Hmi.Screen.Screen? FindHmiScreen(HmiTarget hmi, string screenName, out List<string> available)
        {
            var screens = new List<object>();
            CollectScreens(hmi.ScreenFolder.Screens, hmi.ScreenFolder.Folders, screens, "");
            available = screens.OfType<global::Siemens.Engineering.Hmi.Screen.Screen>().Select(s => s.Name).ToList();
            return screens
                .OfType<global::Siemens.Engineering.Hmi.Screen.Screen>()
                .FirstOrDefault(s => s.Name.Equals(screenName, StringComparison.OrdinalIgnoreCase));
        }

        public object? GetScreenByName(string softwarePath, string screenName)
        {
            _logger?.LogInformation($"Getting HMI screen '{screenName}'");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var hmi = GetHmiTarget(softwarePath);
            if (hmi == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
            }

            var screen = FindHmiScreen(hmi, screenName, out var available);
            if (screen == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"HMI screen '{screenName}' not found", available);
            }

            return screen;
        }

        public string ExportScreen(string softwarePath, string screenName, string exportPath)
        {
            _logger?.LogInformation($"Exporting HMI screen '{screenName}' to '{exportPath}'");

            try
            {
                var screen = GetScreenByName(softwarePath, screenName) as global::Siemens.Engineering.Hmi.Screen.Screen;

                var file = ResolveExportFile(exportPath, screen!.Name);
                screen.Export(new FileInfo(file), ExportOptions.WithDefaults);
                return file;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, $"Failed to export HMI screen '{screenName}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["screenName"] = screenName;
                pex.Data["exportPath"] = exportPath;
                _logger?.LogError(pex, "ExportScreen failed for {SoftwarePath} {ScreenName} -> {ExportPath}", softwarePath, screenName, exportPath);
                throw pex;
            }
        }

        public List<string> ImportScreen(string softwarePath, string importPath)
        {
            _logger?.LogInformation($"Importing HMI screen from '{importPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var hmi = GetHmiTarget(softwarePath);
                if (hmi == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
                }

                var fileInfo = new FileInfo(importPath);
                if (!fileInfo.Exists)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                }

                var imported = RunWithTimeout(
                    () => hmi.ScreenFolder.Screens.Import(fileInfo, ImportOptions.Override),
                    60, "ImportScreen");

                if (imported == null || imported.Count == 0)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Import returned no screens (check the SimaticML content)");
                }

                return imported.Select(s => s.Name).ToList();
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, $"Failed to import HMI screen from '{importPath}': {DescribeException(ex)}", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportScreen failed for {SoftwarePath} from {ImportPath}", softwarePath, importPath);
                throw pex;
            }
        }

        public object? GetScreenInfo(string softwarePath, string screenName)
        {
            return GetScreenByName(softwarePath, screenName);
        }

        #endregion

        #region HMI Screen Templates

        private void CollectScreenTemplates(
            global::Siemens.Engineering.Hmi.Screen.ScreenTemplateComposition templates,
            global::Siemens.Engineering.Hmi.Screen.ScreenTemplateUserFolderComposition folders,
            List<object> list,
            string regexName)
        {
            if (templates != null)
            {
                foreach (global::Siemens.Engineering.Hmi.Screen.ScreenTemplate template in templates)
                {
                    if (!string.IsNullOrEmpty(regexName) && !SafeRegexMatch(template.Name, regexName))
                    {
                        continue;
                    }
                    list.Add(template);
                }
            }

            if (folders != null)
            {
                foreach (global::Siemens.Engineering.Hmi.Screen.ScreenTemplateUserFolder folder in folders)
                {
                    CollectScreenTemplates(folder.ScreenTemplates, folder.Folders, list, regexName);
                }
            }
        }

        public List<object> GetScreenTemplates(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting HMI screen templates...");

            if (IsProjectNull())
            {
                return [];
            }

            var hmi = GetHmiTarget(softwarePath);
            if (hmi == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
            }

            var list = new List<object>();
            CollectScreenTemplates(hmi.ScreenTemplateFolder.ScreenTemplates, hmi.ScreenTemplateFolder.Folders, list, regexName);
            return list;
        }

        public string ExportScreenTemplate(string softwarePath, string templateName, string exportPath)
        {
            _logger?.LogInformation($"Exporting HMI screen template '{templateName}' to '{exportPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var hmi = GetHmiTarget(softwarePath);
                if (hmi == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
                }

                var templates = new List<object>();
                CollectScreenTemplates(hmi.ScreenTemplateFolder.ScreenTemplates, hmi.ScreenTemplateFolder.Folders, templates, "");
                var template = templates
                    .OfType<global::Siemens.Engineering.Hmi.Screen.ScreenTemplate>()
                    .FirstOrDefault(t => t.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase));

                if (template == null)
                {
                    var names = templates.OfType<global::Siemens.Engineering.Hmi.Screen.ScreenTemplate>().Select(t => t.Name);
                    throw new PortalException(PortalErrorCode.NotFound, $"HMI screen template '{templateName}' not found", names);
                }

                var file = ResolveExportFile(exportPath, template.Name);
                template.Export(new FileInfo(file), ExportOptions.WithDefaults);
                return file;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, $"Failed to export HMI screen template '{templateName}': {DescribeException(ex)}", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["templateName"] = templateName;
                pex.Data["exportPath"] = exportPath;
                _logger?.LogError(pex, "ExportScreenTemplate failed for {TemplateName}", templateName);
                throw pex;
            }
        }

        public List<string> ImportScreenTemplate(string softwarePath, string importPath)
        {
            _logger?.LogInformation($"Importing HMI screen template from '{importPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var hmi = GetHmiTarget(softwarePath);
                if (hmi == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
                }

                var fileInfo = new FileInfo(importPath);
                if (!fileInfo.Exists)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                }

                var imported = RunWithTimeout(
                    () => hmi.ScreenTemplateFolder.ScreenTemplates.Import(fileInfo, ImportOptions.Override),
                    120, "ImportScreenTemplate");

                if (imported == null || imported.Count == 0)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Import returned no screen templates (check the SimaticML content)");
                }

                return imported.Select(t => t.Name).ToList();
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, $"Failed to import HMI screen template from '{importPath}': {DescribeException(ex)}", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportScreenTemplate failed from {ImportPath}", importPath);
                throw pex;
            }
        }

        public string ExportHmiConnection(string softwarePath, string connectionName, string exportPath)
        {
            _logger?.LogInformation($"Exporting HMI connection '{connectionName}' to '{exportPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var hmi = GetHmiTarget(softwarePath);
                if (hmi == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
                }

                global::Siemens.Engineering.Hmi.Communication.Connection? connection = null;
                var available = new List<string>();
                foreach (global::Siemens.Engineering.Hmi.Communication.Connection c in hmi.Connections)
                {
                    available.Add(c.Name);
                    if (c.Name.Equals(connectionName, StringComparison.OrdinalIgnoreCase))
                    {
                        connection = c;
                    }
                }

                if (connection == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"HMI connection '{connectionName}' not found", available);
                }

                var file = ResolveExportFile(exportPath, connection.Name);
                connection.Export(new FileInfo(file), ExportOptions.WithDefaults);
                return file;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, $"Failed to export HMI connection '{connectionName}': {DescribeException(ex)}", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["connectionName"] = connectionName;
                pex.Data["exportPath"] = exportPath;
                _logger?.LogError(pex, "ExportHmiConnection failed for {ConnectionName}", connectionName);
                throw pex;
            }
        }

        public List<string> ImportHmiConnection(string softwarePath, string importPath)
        {
            _logger?.LogInformation($"Importing HMI connection from '{importPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var hmi = GetHmiTarget(softwarePath);
                if (hmi == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
                }

                var fileInfo = new FileInfo(importPath);
                if (!fileInfo.Exists)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                }

                var imported = RunWithTimeout(
                    () => hmi.Connections.Import(fileInfo, ImportOptions.Override),
                    60, "ImportHmiConnection");

                if (imported == null || imported.Count == 0)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Import returned no connections (check the SimaticML content)");
                }

                return imported.Select(c => c.Name).ToList();
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, $"Failed to import HMI connection from '{importPath}': {DescribeException(ex)}", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportHmiConnection failed from {ImportPath}", importPath);
                throw pex;
            }
        }

        #endregion

        #region object model introspection

        /// <summary>
        /// Dumps the attribute infos, composition names (with counts) and service infos
        /// of an HMI/PLC object - the discovery tool for what the installed Openness
        /// version actually exposes (e.g. where alarms live on a Basic panel).
        /// </summary>
        public string DebugInspect(string softwarePath, string kind, string name = "", string parentName = "")
        {
            _logger?.LogInformation($"Inspecting {kind} '{name}'");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            IEngineeringObject? obj = kind.ToLowerInvariant() switch
            {
                "hmitarget" => GetHmiTarget(softwarePath),
                "tagtable" => FindHmiTagTable(GetHmiTarget(softwarePath)!, name, out _),
                "tag" => FindHmiTagTable(GetHmiTarget(softwarePath)!, parentName, out _)?.Tags
                    .OfType<global::Siemens.Engineering.Hmi.Tag.Tag>()
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)),
                "screen" => GetScreenByName(softwarePath, name) as IEngineeringObject,
                "connection" => GetHmiConnections(softwarePath)
                    .OfType<global::Siemens.Engineering.Hmi.Communication.Connection>()
                    .FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)),
                "plcsoftware" => GetPlcSoftware(softwarePath),
                "block" => GetBlock(softwarePath, name),
                _ => throw new PortalException(PortalErrorCode.InvalidParams,
                    $"Unknown kind '{kind}'. Use one of: HmiTarget, TagTable, Tag, Screen, Connection, PlcSoftware, Block.")
            };

            if (obj == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"{kind} '{name}' not found at '{softwarePath}'");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Type: {obj.GetType().FullName}");

            sb.AppendLine("Attributes:");
            try
            {
                foreach (var info in obj.GetAttributeInfos())
                {
                    string value;
                    try { value = obj.GetAttribute(info.Name)?.ToString() ?? "<null>"; }
                    catch (Exception ex) { value = $"<unreadable: {ex.Message}>"; }
                    sb.AppendLine($"  {info.Name} [{info.AccessMode}] = {value}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  <GetAttributeInfos failed: {ex.Message}>");
            }

            sb.AppendLine("Compositions:");
            try
            {
                foreach (var info in obj.GetCompositionInfos())
                {
                    var count = "?";
                    try
                    {
                        if (obj.GetComposition(info.Name) is System.Collections.IEnumerable composition)
                        {
                            count = composition.Cast<object>().Count().ToString();
                        }
                    }
                    catch (Exception)
                    {
                    }
                    sb.AppendLine($"  {info.Name} (count: {count})");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  <GetCompositionInfos failed: {ex.Message}>");
            }

            if (obj is IEngineeringServiceProvider provider)
            {
                sb.AppendLine("Services:");
                try
                {
                    foreach (var info in provider.GetServiceInfos())
                    {
                        sb.AppendLine($"  {info.Type?.FullName ?? info.ToString()}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  <GetServiceInfos failed: {ex.Message}>");
                }
            }

            return sb.ToString();
        }

        #endregion

        #region HMI Connections

        public List<object> GetHmiConnections(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting HMI connections...");

            if (IsProjectNull())
            {
                return [];
            }

            var hmi = GetHmiTarget(softwarePath);
            if (hmi == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
            }

            var list = new List<object>();
            if (hmi.Connections != null)
            {
                foreach (global::Siemens.Engineering.Hmi.Communication.Connection connection in hmi.Connections)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(connection.Name, regexName, RegexOptions.IgnoreCase))
                        {
                            continue;
                        }
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    list.Add(connection);
                }
            }

            return list;
        }

        public void CreateHmiConnection(string softwarePath, string connectionName, string partnerDevicePath)
        {
            // Verified against V20: ConnectionComposition has no Create, and the
            // INTEGRATED connection (the one required for symbolically bound tags on
            // optimized DBs) is not exposed to Openness at all - it does not even appear
            // in hmi.Connections on a panel that has one. There is no project-level
            // connection-creation API either (Siemens.Engineering.Connection.* is the
            // online-access configuration, not project connections).
            throw new PortalException(PortalErrorCode.InvalidState,
                "TIA Openness cannot create HMI connections. For PLC-bound (symbolic) HMI tags you need an INTEGRATED " +
                "connection: open 'Devices & networks > Connections', select 'HMI connection' and drag a line from the " +
                "HMI's PROFINET port to the PLC's - one manual step per panel. (A manually-added non-integrated " +
                "connection with only an IP address can NOT resolve symbolic tags on optimized DBs.) " +
                "ImportHmiTagTable/ImportScreen work against the integrated connection once it exists; " +
                "ExportHmiConnection/ImportHmiConnection only round-trip non-integrated connections.");
        }

        #endregion

        #region HMI Alarms

        // Classic (Basic/Comfort) panels do not expose alarms as Openness objects -
        // discrete and analog alarms are attached to HMI tags and ride along in the
        // tag-table SimaticML. Reading them = export tag tables to temp XML and parse;
        // creating them = ImportHmiTagTable with the alarm elements included.
        // Unified panels expose them as first-class compositions on HmiSoftware.
        public List<object> GetDiscreteAlarms(string softwarePath, string regexName = "")
        {
            return GetHmiAlarms(softwarePath, "DiscreteAlarm", regexName);
        }

        public List<object> GetAnalogAlarms(string softwarePath, string regexName = "")
        {
            return GetHmiAlarms(softwarePath, "AnalogAlarm", regexName);
        }

        private List<object> GetHmiAlarms(string softwarePath, string alarmKind, string regexName)
        {
            _logger?.LogInformation($"Getting HMI {alarmKind}s...");

            if (IsProjectNull())
            {
                return [];
            }

            // Unified HMI: first-class alarm objects
            var unified = GetHmiSoftware(softwarePath);
            if (unified != null)
            {
                var list = new List<object>();
                System.Collections.IEnumerable alarms = alarmKind == "DiscreteAlarm"
                    ? unified.DiscreteAlarms
                    : (System.Collections.IEnumerable)unified.AnalogAlarms;
                foreach (IEngineeringObject alarm in alarms)
                {
                    string name = "";
                    try { name = alarm.GetAttribute("Name")?.ToString() ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(regexName) && !SafeRegexMatch(name, regexName))
                    {
                        continue;
                    }
                    list.Add(alarm);
                }
                return list;
            }

            var hmi = GetHmiTarget(softwarePath);
            if (hmi == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"No HMI software found at path '{softwarePath}'");
            }

            // Classic HMI: harvest alarms from the tag-table SimaticML
            var result = new List<object>();
            var tables = new List<object>();
            CollectHmiTagTables(hmi.TagFolder.TagTables, hmi.TagFolder.Folders, tables, "");

            var tempDir = Path.Combine(Path.GetTempPath(), "tia_hmialarms_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                foreach (var t in tables.OfType<global::Siemens.Engineering.Hmi.Tag.TagTable>())
                {
                    var file = Path.Combine(tempDir, SanitizeFileName(t.Name) + ".xml");
                    try
                    {
                        t.Export(new FileInfo(file), ExportOptions.WithDefaults);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, $"Skipping tag table '{t.Name}' (export failed)");
                        continue;
                    }

                    var doc = XDocument.Load(file);
                    foreach (var alarmEl in doc.Descendants().Where(e => e.Name.LocalName.EndsWith(alarmKind, StringComparison.Ordinal)))
                    {
                        var info = new Dictionary<string, object?>
                        {
                            ["TagTable"] = t.Name,
                            ["AlarmKind"] = alarmKind
                        };

                        // trigger tag = the enclosing Hmi.Tag.Tag object's Name
                        var tagEl = alarmEl.Ancestors().FirstOrDefault(a => a.Name.LocalName.EndsWith(".Tag", StringComparison.Ordinal));
                        var tagName = tagEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList")?
                            .Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
                        info["TriggerTag"] = tagName;

                        var attrList = alarmEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                        if (attrList != null)
                        {
                            foreach (var attr in attrList.Elements())
                            {
                                if (!attr.HasElements)
                                {
                                    info[attr.Name.LocalName] = attr.Value;
                                }
                            }
                        }

                        // multilingual alarm text(s)
                        var texts = alarmEl.Descendants()
                            .Where(e => e.Name.LocalName == "MultilingualTextItem")
                            .Select(item => item.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value)
                            .Where(v => !string.IsNullOrEmpty(v))
                            .Distinct()
                            .ToList();
                        if (texts.Count > 0)
                        {
                            info["AlarmText"] = string.Join(" | ", texts);
                        }

                        var alarmName = info.TryGetValue("Name", out var n) ? n?.ToString() ?? "" : "";
                        if (!string.IsNullOrEmpty(regexName) &&
                            !SafeRegexMatch(alarmName, regexName) &&
                            !SafeRegexMatch(tagName ?? "", regexName))
                        {
                            continue;
                        }

                        result.Add(info);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }

            return result;
        }

        private static bool SafeRegexMatch(string input, string pattern)
        {
            try
            {
                return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        #endregion

        #region HMI Text Lists

        public List<object> GetTextLists(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting HMI text lists...");

            if (IsProjectNull())
            {
                return [];
            }

            var hmi = GetHmiTarget(softwarePath);
            if (hmi == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
            }

            var list = new List<object>();
            if (hmi.TextLists != null)
            {
                foreach (global::Siemens.Engineering.Hmi.TextGraphicList.TextList textList in hmi.TextLists)
                {
                    if (!string.IsNullOrEmpty(regexName) && !SafeRegexMatch(textList.Name, regexName))
                    {
                        continue;
                    }
                    list.Add(textList);
                }
            }

            return list;
        }

        private global::Siemens.Engineering.Hmi.TextGraphicList.TextList? FindTextList(HmiTarget hmi, string textListName, out List<string> available)
        {
            available = new List<string>();
            global::Siemens.Engineering.Hmi.TextGraphicList.TextList? found = null;
            if (hmi.TextLists != null)
            {
                foreach (global::Siemens.Engineering.Hmi.TextGraphicList.TextList textList in hmi.TextLists)
                {
                    available.Add(textList.Name);
                    if (textList.Name.Equals(textListName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = textList;
                    }
                }
            }
            return found;
        }

        public string ExportTextList(string softwarePath, string textListName, string exportPath)
        {
            _logger?.LogInformation($"Exporting HMI text list '{textListName}' to '{exportPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var hmi = GetHmiTarget(softwarePath);
                if (hmi == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
                }

                var textList = FindTextList(hmi, textListName, out var available);
                if (textList == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"HMI text list '{textListName}' not found", available);
                }

                var file = ResolveExportFile(exportPath, textList.Name);
                textList.Export(new FileInfo(file), ExportOptions.WithDefaults);
                return file;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, $"Failed to export HMI text list '{textListName}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["textListName"] = textListName;
                pex.Data["exportPath"] = exportPath;
                _logger?.LogError(pex, "ExportTextList failed for {SoftwarePath} {TextListName} -> {ExportPath}", softwarePath, textListName, exportPath);
                throw pex;
            }
        }

        public List<string> ImportTextList(string softwarePath, string importPath)
        {
            _logger?.LogInformation($"Importing HMI text list from '{importPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var hmi = GetHmiTarget(softwarePath);
                if (hmi == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No classic HMI target found at path '{softwarePath}'");
                }

                var fileInfo = new FileInfo(importPath);
                if (!fileInfo.Exists)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                }

                var imported = RunWithTimeout(
                    () => hmi.TextLists.Import(fileInfo, ImportOptions.Override),
                    60, "ImportTextList");

                if (imported == null || imported.Count == 0)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, "Import returned no text lists (check the SimaticML content)");
                }

                return imported.Select(t => t.Name).ToList();
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, $"Failed to import HMI text list from '{importPath}': {DescribeException(ex)}", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportTextList failed for {SoftwarePath} from {ImportPath}", softwarePath, importPath);
                throw pex;
            }
        }

        #endregion

        #endregion


        #region technology objects

        private void CollectTechnologyObjects(
            global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBGroup group,
            List<object> list, string regexName)
        {
            if (group == null)
            {
                return;
            }

            if (group.TechnologicalObjects != null)
            {
                foreach (global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB to in group.TechnologicalObjects)
                {
                    if (!string.IsNullOrEmpty(regexName) && !SafeRegexMatch(to.Name, regexName))
                    {
                        continue;
                    }
                    list.Add(to);
                }
            }

            if (group.Groups != null)
            {
                foreach (global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBUserGroup sub in group.Groups)
                {
                    CollectTechnologyObjects(sub, list, regexName);
                }
            }
        }

        public List<object> GetTechnologyObjects(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting technology objects...");

            if (IsProjectNull())
            {
                return [];
            }

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"No PLC software found at path '{softwarePath}'");
            }

            var list = new List<object>();
            CollectTechnologyObjects(plcSoftware.TechnologicalObjectGroup, list, regexName);
            return list;
        }

        public object? GetTechnologyObject(string softwarePath, string objectName)
        {
            _logger?.LogInformation($"Getting technology object '{objectName}'");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var all = GetTechnologyObjects(softwarePath);
            var found = all
                .OfType<global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB>()
                .FirstOrDefault(t => t.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase));

            if (found == null)
            {
                var names = all.OfType<global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB>().Select(t => t.Name);
                throw new PortalException(PortalErrorCode.NotFound, $"Technology object '{objectName}' not found", names);
            }

            return found;
        }

        public object? ExportTechnologyObject(string softwarePath, string objectName, string exportPath)
        {
            _logger?.LogInformation($"Exporting technology object '{objectName}' to '{exportPath}'");

            try
            {
                var to = GetTechnologyObject(softwarePath, objectName) as global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB;

                var file = ResolveExportFile(exportPath, to!.Name);
                to.Export(new FileInfo(file), ExportOptions.WithDefaults);
                return file;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, $"Failed to export technology object '{objectName}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["objectName"] = objectName;
                pex.Data["exportPath"] = exportPath;
                _logger?.LogError(pex, "ExportTechnologyObject failed for {ObjectName}", objectName);
                throw pex;
            }
        }

        public bool ImportTechnologyObject(string softwarePath, string importPath)
        {
            _logger?.LogInformation($"Importing technology object from '{importPath}'");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var plcSoftware = GetPlcSoftware(softwarePath);
                if (plcSoftware == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound, $"No PLC software found at path '{softwarePath}'");
                }

                var fileInfo = new FileInfo(importPath);
                if (!fileInfo.Exists)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                }

                var imported = RunWithTimeout(
                    () => plcSoftware.TechnologicalObjectGroup.TechnologicalObjects.Import(fileInfo, ImportOptions.Override),
                    60, "ImportTechnologyObject");

                return imported != null && imported.Count > 0;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, $"Failed to import technology object from '{importPath}'", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportTechnologyObject failed from {ImportPath}", importPath);
                throw pex;
            }
        }

        public bool DeleteTechnologyObject(string softwarePath, string objectName)
        {
            // V20 Openness exposes TechnologicalInstanceDB without a Delete method.
            // The instance DB it owns is visible under program blocks though, so the
            // object can be removed by deleting that DB.
            throw new PortalException(PortalErrorCode.InvalidState,
                "TIA Openness V20 does not expose deleting technology objects (TechnologicalInstanceDB has no Delete). " +
                "Delete the technology object in the TIA Portal UI, or try DeleteBlock on its instance DB under Program blocks > System blocks.");
        }

        #endregion

        #region safety programming

        public SafetyAdministration? GetSafetyAdministration(string softwarePath)
        {
            _logger?.LogInformation($"Getting safety administration for: {softwarePath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var softwareContainer = GetSoftwareContainer(softwarePath);
                var deviceItem = softwareContainer?.Parent as DeviceItem;

                if (deviceItem != null)
                {
                    var admin = deviceItem.GetService<SafetyAdministration>();
                    return admin;
                }

                return null;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Failed to get safety administration", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                _logger?.LogError(pex, "GetSafetyAdministration failed for {SoftwarePath}", softwarePath);
                throw pex;
            }
        }

        public Dictionary<string, object?> GetSafetySettings(string softwarePath)
        {
            _logger?.LogInformation($"Getting safety settings for: {softwarePath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var admin = GetSafetyAdministration(softwarePath);
                var settings = new Dictionary<string, object?>();

                if (admin != null)
                {
                    settings["IsLoggedOnToSafetyOfflineProgram"] = admin.IsLoggedOnToSafetyOfflineProgram;

                    try
                    {
                        var runtimeGroups = admin.RuntimeGroups;
                        if (runtimeGroups != null)
                        {
                            var groupList = new List<string>();
                            foreach (var rg in runtimeGroups)
                            {
                                groupList.Add(rg.Name);
                            }
                            settings["RuntimeGroups"] = groupList;
                        }
                    }
                    catch
                    {
                        // RuntimeGroups may not be available on all configurations
                    }
                }
                else
                {
                    settings["SafetySupported"] = false;
                }

                return settings;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Failed to get safety settings", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                _logger?.LogError(pex, "GetSafetySettings failed for {SoftwarePath}", softwarePath);
                throw pex;
            }
        }

        public Dictionary<string, object?> GetSafetyInfo(string softwarePath)
        {
            _logger?.LogInformation($"Getting safety info for: {softwarePath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var info = new Dictionary<string, object?>();
                var admin = GetSafetyAdministration(softwarePath);

                info["IsSafetyEnabled"] = admin != null;

                if (admin != null)
                {
                    info["IsLoggedOnToSafetyOfflineProgram"] = admin.IsLoggedOnToSafetyOfflineProgram;
                }

                // Count safety-relevant blocks
                var safetyBlocks = GetSafetyBlocks(softwarePath);
                info["SafetyBlockCount"] = safetyBlocks.Count;

                return info;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Failed to get safety info", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                _logger?.LogError(pex, "GetSafetyInfo failed for {SoftwarePath}", softwarePath);
                throw pex;
            }
        }

        public bool SetSafetyPassword(string softwarePath, string password)
        {
            _logger?.LogInformation($"Setting safety password for: {softwarePath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var admin = GetSafetyAdministration(softwarePath);

                if (admin == null)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Safety administration is not available for this PLC");
                }

                SecureString secString = new NetworkCredential("", password).SecurePassword;

                if (!admin.IsLoggedOnToSafetyOfflineProgram)
                {
                    admin.LoginToSafetyOfflineProgram(secString);
                }

                return true;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Failed to set safety password", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                _logger?.LogError(pex, "SetSafetyPassword failed for {SoftwarePath}", softwarePath);
                throw pex;
            }
        }

        public List<PlcBlock> GetSafetyBlocks(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting safety blocks...");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var allBlocks = GetBlocks(softwarePath, regexName);
                var safetyBlocks = new List<PlcBlock>();

                foreach (var block in allBlocks)
                {
                    try
                    {
                        var isSafety = block.GetAttribute("SetpointSafety");
                        if (isSafety is bool safety && safety)
                        {
                            safetyBlocks.Add(block);
                        }
                    }
                    catch
                    {
                        // Attribute may not exist on non-safety blocks, skip
                    }
                }

                return safetyBlocks;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Failed to get safety blocks", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                _logger?.LogError(pex, "GetSafetyBlocks failed for {SoftwarePath}", softwarePath);
                throw pex;
            }
        }

        #endregion



    }


}
