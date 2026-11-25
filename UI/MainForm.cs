using DependencyManager.Models;
using DependencyManager.Services;
using DependencyManager.Storage;
using System.Text.RegularExpressions;

namespace DependencyManager.UI;

internal sealed class MainForm : Form
{
    private const string SpringBootPluginGroup = "org.springframework.boot";
    private const string SpringBootPluginArtifact = "spring-boot-gradle-plugin";

    private readonly TextBox _filePathTextBox = new();
    private readonly Button _browseButton = new();
    private readonly Button _analyzeButton = new();
    private readonly Button _refreshButton = new();
    private readonly ComboBox _springBootComboBox = new();
    private readonly Label _bootLabel = new();
    private readonly Label _statusLabel = new();
    private readonly DataGridView _dependenciesGrid = new();
    private readonly RichTextBox _logTextBox = new();
    private readonly Button _clearLogButton = new();

    private readonly HttpCache _cache;
    private readonly MavenCentralClient _maven;
    private readonly BomResolver _bomResolver;
    private GradleFileAnalysis? _analysis;
    private IReadOnlyDictionary<string, string> _currentManagedVersions = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> _managedVersions = new Dictionary<string, string>();
    private bool _currentBomLoaded;
    private bool _busy;
    private bool _suppressBootEvent;
    private bool _gridReady;
    private CancellationTokenSource? _operationCts;

    public MainForm()
    {
        Text = "Dependency Manager — build.gradle analyzer";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1180, 700);
        Size = new Size(1580, 950);
        AutoScaleMode = AutoScaleMode.Dpi;

        StoragePaths.EnsureDirectories();
        _cache = new HttpCache(StoragePaths.DatabasePath);
        _maven = new MavenCentralClient(_cache, AppendLog);
        _bomResolver = new BomResolver(_maven);

        BuildLayout();
        ConfigureGrid();
        AppendLog("INFO", "Ready. Select a build.gradle or build.gradle.kts file.");
        AppendLog("INFO", "This application reads the selected file only. It never starts Gradle or Java.");
        Shown += async (_, _) => await LoadSpringBootVersionsAsync();
        FormClosing += (_, _) => _operationCts?.Cancel();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        var filePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0)
        };
        filePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        filePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var fileLabel = new Label
        {
            Text = "Gradle build file (read-only)",
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        filePanel.Controls.Add(fileLabel, 0, 0);

        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Margin = new Padding(0)
        };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));

        _filePathTextBox.Dock = DockStyle.Fill;
        _filePathTextBox.PlaceholderText = @"D:\Projects\MyApp\build.gradle";
        _filePathTextBox.Margin = new Padding(0, 2, 8, 2);
        _filePathTextBox.TextChanged += (_, _) => InvalidateAnalysisIfFileChanged();

        _browseButton.Text = "Browse...";
        _browseButton.Dock = DockStyle.Fill;
        _browseButton.Margin = new Padding(0, 2, 8, 2);
        _browseButton.Click += BrowseButton_Click;

        _analyzeButton.Text = "Analyze";
        _analyzeButton.Dock = DockStyle.Fill;
        _analyzeButton.Margin = new Padding(0, 2, 0, 2);
        _analyzeButton.Click += async (_, _) => await AnalyzeButton_ClickAsync();

        pathRow.Controls.Add(_filePathTextBox, 0, 0);
        pathRow.Controls.Add(_browseButton, 1, 0);
        pathRow.Controls.Add(_analyzeButton, 2, 0);
        filePanel.Controls.Add(pathRow, 0, 1);

        var platformPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(8, 5, 8, 5),
            BackColor = SystemColors.ControlLight
        };
        platformPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        platformPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        platformPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        platformPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        platformPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));

        var bootTitle = new Label
        {
            Text = "Spring Boot version",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold)
        };
        _springBootComboBox.Dock = DockStyle.Fill;
        _springBootComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _springBootComboBox.Enabled = false;
        _springBootComboBox.SelectedIndexChanged += SpringBootComboBox_SelectedIndexChanged;

        _bootLabel.Dock = DockStyle.Fill;
        _bootLabel.TextAlign = ContentAlignment.MiddleLeft;
        _bootLabel.Text = "Waiting for build.gradle analysis";
        _bootLabel.ForeColor = SystemColors.GrayText;

        _refreshButton.Text = "Refresh online data";
        _refreshButton.Dock = DockStyle.Fill;
        _refreshButton.Enabled = true;
        _refreshButton.Click += async (_, _) => await RefreshOnlineDataAsync();

        platformPanel.Controls.Add(bootTitle, 0, 0);
        platformPanel.Controls.Add(_springBootComboBox, 1, 0);
        platformPanel.Controls.Add(_bootLabel, 3, 0);
        platformPanel.Controls.Add(_refreshButton, 4, 0);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 465,
            SplitterWidth = 6,
            Panel1MinSize = 170,
            Panel2MinSize = 110
        };
        split.Panel1.Controls.Add(_dependenciesGrid);

        var logLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0)
        };
        logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var logHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = new Padding(0)
        };
        logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        var logTitle = new Label
        {
            Text = "Activity log",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold)
        };
        _clearLogButton.Text = "Clear log";
        _clearLogButton.Dock = DockStyle.Fill;
        _clearLogButton.Click += (_, _) => _logTextBox.Clear();
        logHeader.Controls.Add(logTitle, 0, 0);
        logHeader.Controls.Add(_clearLogButton, 1, 0);

        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.ReadOnly = true;
        _logTextBox.WordWrap = false;
        _logTextBox.DetectUrls = false;
        _logTextBox.HideSelection = false;
        _logTextBox.ScrollBars = RichTextBoxScrollBars.Both;
        _logTextBox.BorderStyle = BorderStyle.FixedSingle;
        _logTextBox.Font = new Font("Consolas", 9f);
        _logTextBox.BackColor = Color.FromArgb(25, 28, 33);
        _logTextBox.ForeColor = Color.Gainsboro;
        _logTextBox.MaxLength = 2_000_000;

        logLayout.Controls.Add(logHeader, 0, 0);
        logLayout.Controls.Add(_logTextBox, 0, 1);
        split.Panel2.Controls.Add(logLayout);

        _statusLabel.Text = "Ready";
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        root.Controls.Add(filePanel, 0, 0);
        root.Controls.Add(platformPanel, 0, 1);
        root.Controls.Add(split, 0, 2);
        root.Controls.Add(_statusLabel, 0, 3);
        Controls.Add(root);
    }

    private void ConfigureGrid()
    {
        _dependenciesGrid.Dock = DockStyle.Fill;
        _dependenciesGrid.AllowUserToAddRows = false;
        _dependenciesGrid.AllowUserToDeleteRows = false;
        _dependenciesGrid.AutoGenerateColumns = false;
        _dependenciesGrid.BackgroundColor = SystemColors.Window;
        _dependenciesGrid.BorderStyle = BorderStyle.FixedSingle;
        _dependenciesGrid.ReadOnly = false;
        _dependenciesGrid.RowHeadersVisible = false;
        _dependenciesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _dependenciesGrid.MultiSelect = false;
        _dependenciesGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _dependenciesGrid.EditMode = DataGridViewEditMode.EditOnEnter;

        AddTextColumn("Group", "Group", 225);
        AddTextColumn("Artifact", "Artifact", 255);
        AddTextColumn("Version", "Current version", 120);
        AddTextColumn("Requested", "Requested", 115);
        AddTextColumn("BomVersion", "BOM version", 120);

        var versionColumn = new DataGridViewComboBoxColumn
        {
            Name = "SelectVersion",
            HeaderText = "Select version",
            Width = 190,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            ReadOnly = false
        };
        _dependenciesGrid.Columns.Add(versionColumn);
        AddTextColumn("OriginalLine", "Original build.gradle line", 560);
        _dependenciesGrid.Columns["OriginalLine"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _dependenciesGrid.Columns["OriginalLine"].MinimumWidth = 300;

        foreach (DataGridViewColumn column in _dependenciesGrid.Columns)
        {
            if (column.Name != "SelectVersion")
                column.ReadOnly = true;
        }

        _dependenciesGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_dependenciesGrid.IsCurrentCellDirty)
                _dependenciesGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _dependenciesGrid.CellValueChanged += DependenciesGrid_CellValueChanged;
        _dependenciesGrid.CellFormatting += DependenciesGrid_CellFormatting;
        _dependenciesGrid.DataError += (_, e) =>
        {
            e.ThrowException = false;
            AppendLog("WARN", "A version dropdown value could not be displayed. Refresh the analysis if the problem persists.");
        };
        _gridReady = true;
    }

    private void AddTextColumn(string name, string header, int width)
    {
        _dependenciesGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            SortMode = DataGridViewColumnSortMode.Automatic,
            ReadOnly = true
        });
    }

    private void BrowseButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select build.gradle",
            Filter = "Gradle build files (build.gradle; build.gradle.kts)|build.gradle;build.gradle.kts|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (File.Exists(_filePathTextBox.Text))
            dialog.InitialDirectory = Path.GetDirectoryName(_filePathTextBox.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _filePathTextBox.Text = dialog.FileName;
    }

    private async Task AnalyzeButton_ClickAsync()
    {
        if (_busy)
            return;

        string path = _filePathTextBox.Text.Trim().Trim('"');
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "Select an existing build.gradle or build.gradle.kts file.", "File not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!Path.GetFileName(path).Equals("build.gradle", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(path).Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "The selected file must be named build.gradle or build.gradle.kts.", "Invalid file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _operationCts?.Cancel();
        _operationCts = new CancellationTokenSource();
        CancellationToken token = _operationCts.Token;
        SetBusy(true, "Reading build.gradle...");
        _logTextBox.Clear();
        _dependenciesGrid.Rows.Clear();
        AppendLog("INFO", $"Reading file: {path}");
        AppendLog("INFO", "No Gradle executable, Wrapper, Java process, build script, or project task will be started.");

        try
        {
            string text = await File.ReadAllTextAsync(path, token);
            GradleFileAnalysis parsed = BuildGradleParser.Parse(path, text);
            _analysis = parsed;
            _currentManagedVersions = new Dictionary<string, string>();
            _managedVersions = new Dictionary<string, string>();
            _currentBomLoaded = false;
            _filePathTextBox.Text = parsed.FilePath;

            AppendLog("SUCCESS", $"Read {text.Length:N0} characters from build.gradle.");
            AppendLog("INFO", $"Found {parsed.Dependencies.Count} direct external dependency declarations.");
            AppendLog("INFO", parsed.HasDependencyManagementPlugin
                ? "io.spring.dependency-management plugin was found. Spring Boot BOM management will be considered."
                : "io.spring.dependency-management plugin was not found. Boot BOM recommendations are shown as reference only.");

            foreach (string warning in parsed.Warnings)
                AppendLog("WARN", warning);

            await EnsureSpringBootSelectionAsync(parsed.SpringBootVersion, token);
            await LoadBomAndRefreshDependenciesAsync(token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("WARN", "Analysis cancelled.");
            _statusLabel.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", ex.ToString());
            _statusLabel.Text = "Analysis failed — see Activity log";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadSpringBootVersionsAsync()
    {
        try
        {
            AppendLog("INFO", "Loading Spring Boot Gradle Plugin versions from Maven Central...");
            IReadOnlyList<string> versions = await _maven.GetVersionsAsync(SpringBootPluginGroup, SpringBootPluginArtifact, CancellationToken.None);
            _suppressBootEvent = true;
            string? previous = _springBootComboBox.SelectedItem as string;
            _springBootComboBox.Items.Clear();
            foreach (string version in versions.Reverse())
                _springBootComboBox.Items.Add(version);
            if (previous is not null && _springBootComboBox.Items.Contains(previous))
                _springBootComboBox.SelectedItem = previous;
            _suppressBootEvent = false;
            _springBootComboBox.Enabled = true;
            AppendLog("SUCCESS", $"Loaded {versions.Count} Spring Boot versions.");
        }
        catch (Exception ex)
        {
            _suppressBootEvent = false;
            AppendLog("ERROR", $"Could not load Spring Boot versions: {ex.Message}");
            _bootLabel.Text = "Online version list unavailable";
        }
    }

    private async Task EnsureSpringBootSelectionAsync(string? detectedVersion, CancellationToken token)
    {
        if (_springBootComboBox.Items.Count == 0)
            await LoadSpringBootVersionsAsync();

        _suppressBootEvent = true;
        if (!string.IsNullOrWhiteSpace(detectedVersion))
        {
            if (!_springBootComboBox.Items.Contains(detectedVersion))
                _springBootComboBox.Items.Insert(0, detectedVersion);
            _springBootComboBox.SelectedItem = detectedVersion;
            _bootLabel.Text = "Detected in plugins {}";
        }
        else if (_springBootComboBox.Items.Count > 0 && _springBootComboBox.SelectedIndex < 0)
        {
            _springBootComboBox.SelectedIndex = 0;
            _bootLabel.Text = "No Spring Boot version detected; latest release selected as reference";
        }
        _springBootComboBox.Enabled = _springBootComboBox.Items.Count > 0;
        _suppressBootEvent = false;
        await Task.CompletedTask;
    }

    private async void SpringBootComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressBootEvent || _analysis is null || _busy || _springBootComboBox.SelectedItem is not string)
            return;

        _operationCts?.Cancel();
        _operationCts = new CancellationTokenSource();
        SetBusy(true, "Loading selected Spring Boot BOM...");
        try
        {
            await LoadBomAndRefreshDependenciesAsync(_operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("WARN", "BOM refresh cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", ex.ToString());
            _statusLabel.Text = "BOM analysis failed — see Activity log";
        }
        finally { SetBusy(false); }
    }

    private async Task LoadBomAndRefreshDependenciesAsync(CancellationToken token)
    {
        if (_analysis is null)
            return;

        string? bootVersion = _springBootComboBox.SelectedItem as string;
        _managedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? originalBootVersion = _analysis.SpringBootVersion;

        // Preserve the version set implied by the original build.gradle plugin version.
        // Changing the Boot ComboBox changes recommendations only, never the "Current version" column.
        if (!_currentBomLoaded)
        {
            if (!string.IsNullOrWhiteSpace(originalBootVersion) && _analysis.HasDependencyManagementPlugin)
            {
                try
                {
                    AppendLog("INFO", $"Loading original BOM {originalBootVersion} to determine current BOM-managed versions...");
                    _currentManagedVersions = await _bomResolver.ResolveSpringBootBomAsync(originalBootVersion, _analysis.PropertyOverrides, token);
                    _currentBomLoaded = true;
                }
                catch (Exception ex)
                {
                    AppendLog("WARN", $"Could not resolve the original Spring Boot BOM {originalBootVersion}: {ex.Message}");
                }
            }
            else
            {
                _currentManagedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _currentBomLoaded = true;
                if (!_analysis.HasDependencyManagementPlugin)
                    AppendLog("WARN", "No io.spring.dependency-management plugin detected. The selected Spring Boot BOM is treated as a recommendation only, not as the project's confirmed current version management.");
            }
        }

        if (!string.IsNullOrWhiteSpace(bootVersion))
        {
            _bootLabel.Text = $"Loading BOM {bootVersion}...";
            AppendLog("INFO", $"Resolving selected Spring Boot dependency BOM {bootVersion}...");
            if (_analysis.HasDependencyManagementPlugin && originalBootVersion is not null &&
                originalBootVersion.Equals(bootVersion, StringComparison.OrdinalIgnoreCase) && _currentBomLoaded)
            {
                _managedVersions = _currentManagedVersions;
            }
            else
            {
                try
                {
                    _managedVersions = await _bomResolver.ResolveSpringBootBomAsync(bootVersion, _analysis.PropertyOverrides, token);
                }
                catch (Exception ex)
                {
                    AppendLog("ERROR", $"Could not resolve selected Spring Boot BOM {bootVersion}: {ex.Message}");
                    _bootLabel.Text = "BOM unavailable — recommendations may be incomplete";
                }
            }
            AppendLog("SUCCESS", $"Selected BOM {bootVersion}: {_managedVersions.Count:N0} managed artifact versions.");
        }
        else
        {
            _bootLabel.Text = "Spring Boot version not detected";
        }

        await LoadArtifactMetadataAsync(_analysis.Dependencies, token);
        RenderDependencies();

        int managedCount = _analysis.Dependencies.Count(dependency => dependency.BomVersion is not null);
        int unknownCount = _analysis.Dependencies.Count(dependency => dependency.IsUnverified);
        _statusLabel.Text = $"{_analysis.Dependencies.Count} declared dependencies | {managedCount} BOM-managed | {unknownCount} unverified";
        if (!string.IsNullOrWhiteSpace(bootVersion) && _bootLabel.Text.StartsWith("Loading", StringComparison.Ordinal))
            _bootLabel.Text = $"Selected BOM {bootVersion} | file plugin: {_analysis.SpringBootVersion ?? "not detected"}";
    }

    private async Task LoadArtifactMetadataAsync(IReadOnlyList<DependencyItem> dependencies, CancellationToken token)
    {
        var unique = dependencies
            .GroupBy(dependency => $"{dependency.Group}:{dependency.Artifact}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        using var semaphore = new SemaphoreSlim(5, 5);
        int completed = 0;

        await Task.WhenAll(unique.Select(async dependency =>
        {
            await semaphore.WaitAsync(token);
            try
            {
                string ga = $"{dependency.Group}:{dependency.Artifact}";
                AppendLog("INFO", $"Querying Maven Central: {ga}");
                IReadOnlyList<string> versions = await _maven.GetVersionsAsync(dependency.Group, dependency.Artifact, token);
                string? managed = _managedVersions.TryGetValue(ga, out string? found) ? found : null;

                foreach (DependencyItem sameArtifact in dependencies.Where(item =>
                             item.Group.Equals(dependency.Group, StringComparison.OrdinalIgnoreCase) &&
                             item.Artifact.Equals(dependency.Artifact, StringComparison.OrdinalIgnoreCase)))
                {
                    sameArtifact.AvailableVersions = versions;
                    sameArtifact.CurrentManagedVersion = _currentManagedVersions.TryGetValue(ga, out string? currentManaged) ? currentManaged : null;
                    sameArtifact.BomVersion = managed;
                    sameArtifact.MetadataError = null;
                }
                AppendLog("SUCCESS", $"{ga}: {versions.Count} published versions" + (managed is null ? "; not listed in selected BOM." : $"; BOM recommends {managed}."));
            }
            catch (Exception ex)
            {
                foreach (DependencyItem sameArtifact in dependencies.Where(item =>
                             item.Group.Equals(dependency.Group, StringComparison.OrdinalIgnoreCase) &&
                             item.Artifact.Equals(dependency.Artifact, StringComparison.OrdinalIgnoreCase)))
                {
                    sameArtifact.MetadataError = ex.Message;
                    string ga = $"{sameArtifact.Group}:{sameArtifact.Artifact}";
                    sameArtifact.CurrentManagedVersion = _currentManagedVersions.TryGetValue(ga, out string? currentManaged) ? currentManaged : null;
                    sameArtifact.BomVersion = _managedVersions.TryGetValue(ga, out string? managed) ? managed : null;
                }
                AppendLog("WARN", $"Could not verify {dependency.Group}:{dependency.Artifact}: {ex.Message}");
            }
            finally
            {
                int count = Interlocked.Increment(ref completed);
                SetStatusFromWorker($"Checking Maven Central: {count}/{unique.Count} artifacts...");
                semaphore.Release();
            }
        }));
    }

    private void RenderDependencies()
    {
        if (!_gridReady || _analysis is null)
            return;

        _dependenciesGrid.Rows.Clear();
        foreach (DependencyItem dependency in _analysis.Dependencies)
        {
            string bomVersion = dependency.BomVersion ?? "";
            string currentVersion = dependency.CurrentVersion;
            string requested = dependency.RequestedVersion;
            int rowIndex = _dependenciesGrid.Rows.Add(
                dependency.Group,
                dependency.DisplayArtifact,
                currentVersion,
                requested,
                bomVersion);

            List<VersionOption> options = CreateVersionOptions(dependency);
            var cell = (DataGridViewComboBoxCell)_dependenciesGrid.Rows[rowIndex].Cells["SelectVersion"];
            cell.DataSource = options;
            cell.DisplayMember = nameof(VersionOption.Display);
            cell.ValueMember = nameof(VersionOption.Version);
            cell.FlatStyle = FlatStyle.Flat;
            cell.DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton;
            _dependenciesGrid.Rows[rowIndex].Cells["OriginalLine"].Value = dependency.OriginalLine;
            _dependenciesGrid.Rows[rowIndex].Tag = dependency;

            string selected = DetermineInitialSelection(dependency, options);
            if (selected.Length > 0)
                cell.Value = selected;

            if (dependency.IsUnverified)
            {
                _dependenciesGrid.Rows[rowIndex].DefaultCellStyle.ForeColor = Color.DarkGoldenrod;
                var reasons = new[] { dependency.ParseWarning, dependency.MetadataError,
                    dependency.BomVersion is null ? "Not managed by the selected Spring Boot BOM; compatibility is not established by the BOM." : null }
                    .Where(value => !string.IsNullOrWhiteSpace(value));
                _dependenciesGrid.Rows[rowIndex].Cells["Artifact"].ToolTipText = string.Join(Environment.NewLine, reasons);
            }
            else if (dependency.IsIncompatibleWithBom)
            {
                _dependenciesGrid.Rows[rowIndex].Cells["Version"].Style.ForeColor = Color.Firebrick;
                _dependenciesGrid.Rows[rowIndex].Cells["Version"].ToolTipText = "Explicitly requested version is on a different major.minor release line than the selected Spring Boot BOM version.";
            }
            else if (dependency.IsOlderPatchThanBom)
            {
                _dependenciesGrid.Rows[rowIndex].Cells["Version"].Style.ForeColor = Color.DarkGoldenrod;
                _dependenciesGrid.Rows[rowIndex].Cells["Version"].ToolTipText = "Explicitly requested version is older than the version managed by the selected Spring Boot BOM.";
            }
        }
    }

    private static List<VersionOption> CreateVersionOptions(DependencyItem dependency)
    {
        var candidates = new List<string>();
        string? managed = dependency.BomVersion;
        string? current = dependency.RequestedVersion;

        if (!string.IsNullOrWhiteSpace(managed))
        {
            candidates.Add(managed);
            candidates.AddRange(FilterSameMaintenanceLine(dependency.AvailableVersions, managed));
        }
        else
        {
            // The artifact is not managed by this BOM; list published versions but flag the row as compatibility-unknown.
            candidates.AddRange(dependency.AvailableVersions.Reverse().Take(150));
        }

        if (managed is null && !string.IsNullOrWhiteSpace(current) && dependency.AvailableVersions.Contains(current, StringComparer.OrdinalIgnoreCase))
            candidates.Add(current);

        return candidates
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => new VersionOption
            {
                Version = value,
                IsBomPreferred = managed is not null && value.Equals(managed, StringComparison.OrdinalIgnoreCase),
                IsCompatibilityUnknown = managed is null
            })
            .ToList();
    }

    private static IEnumerable<string> FilterSameMaintenanceLine(IReadOnlyList<string> versions, string managedVersion)
    {
        Match managed = System.Text.RegularExpressions.Regex.Match(managedVersion, @"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?<suffix>[-+].*)?$");
        if (!managed.Success)
            return Array.Empty<string>();

        int major = int.Parse(managed.Groups["major"].Value);
        int minor = int.Parse(managed.Groups["minor"].Value);

        // Include every published GA patch in the BOM-managed major.minor line.
        // Requiring patch >= BOM patch incorrectly leaves only the BOM version
        // when it is the newest published patch.
        return versions.Where(version =>
        {
            Match candidate = System.Text.RegularExpressions.Regex.Match(version, @"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$");
            return candidate.Success &&
                   int.Parse(candidate.Groups["major"].Value) == major &&
                   int.Parse(candidate.Groups["minor"].Value) == minor;
        }).OrderByDescending(ParsePatch);
    }

    private static int ParsePatch(string version)
    {
        Match match = System.Text.RegularExpressions.Regex.Match(version, @"^\d+\.\d+\.(?<patch>\d+)$");
        return match.Success && int.TryParse(match.Groups["patch"].Value, out int patch) ? patch : 0;
    }

    private static string DetermineInitialSelection(DependencyItem dependency, IReadOnlyList<VersionOption> options)
    {
        if (!string.IsNullOrWhiteSpace(dependency.RequestedVersion) && options.Any(option => option.Version == dependency.RequestedVersion))
            return dependency.RequestedVersion;
        if (!string.IsNullOrWhiteSpace(dependency.BomVersion) && options.Any(option => option.Version == dependency.BomVersion))
            return dependency.BomVersion;
        return options.FirstOrDefault()?.Version ?? "";
    }

    private void DependenciesGrid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _dependenciesGrid.Columns[e.ColumnIndex].Name != "SelectVersion")
            return;
        if (_dependenciesGrid.Rows[e.RowIndex].Tag is not DependencyItem dependency)
            return;
        string? selected = _dependenciesGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString();
        if (!string.IsNullOrWhiteSpace(selected))
            AppendLog("CHOICE", $"Selected {selected} for {dependency.Group}:{dependency.Artifact}. build.gradle was not changed.");
    }

    private void DependenciesGrid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _dependenciesGrid.Rows[e.RowIndex].Tag is not DependencyItem dependency)
            return;
        if (dependency.IsUnverified)
        {
            e.CellStyle.ForeColor = Color.DarkGoldenrod;
            if (_dependenciesGrid.Columns[e.ColumnIndex].Name == "Artifact")
                e.Value = "? " + dependency.Artifact;
        }
    }

    private async Task RefreshOnlineDataAsync()
    {
        if (_busy)
            return;
        _operationCts?.Cancel();
        _operationCts = new CancellationTokenSource();
        SetBusy(true, "Refreshing Maven Central metadata...");
        try
        {
            AppendLog("INFO", "Clearing the local Maven metadata/BOM cache...");
            await _cache.ClearAsync(_operationCts.Token);
            AppendLog("SUCCESS", "Local HTTP cache cleared.");
            if (_analysis is not null)
            {
                _currentManagedVersions = new Dictionary<string, string>();
                _currentBomLoaded = false;
            }
            await LoadSpringBootVersionsAsync();
            if (_analysis is not null)
                await LoadBomAndRefreshDependenciesAsync(_operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("WARN", "Refresh cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", ex.ToString());
        }
        finally { SetBusy(false); }
    }

    private void InvalidateAnalysisIfFileChanged()
    {
        if (_analysis is null)
            return;
        try
        {
            string current = Path.GetFullPath(_filePathTextBox.Text.Trim().Trim('"'));
            if (!current.Equals(_analysis.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _analysis = null;
                _currentManagedVersions = new Dictionary<string, string>();
                _managedVersions = new Dictionary<string, string>();
                _currentBomLoaded = false;
                _dependenciesGrid.Rows.Clear();
                _bootLabel.Text = "File changed — analyze again";
                _statusLabel.Text = "File selection changed";
            }
        }
        catch
        {
            _analysis = null;
            _currentManagedVersions = new Dictionary<string, string>();
            _managedVersions = new Dictionary<string, string>();
            _currentBomLoaded = false;
            _dependenciesGrid.Rows.Clear();
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        _browseButton.Enabled = !busy;
        _analyzeButton.Enabled = !busy;
        _refreshButton.Enabled = !busy;
        _springBootComboBox.Enabled = !busy && _springBootComboBox.Items.Count > 0;
        _filePathTextBox.Enabled = !busy;
        if (!string.IsNullOrWhiteSpace(status))
            _statusLabel.Text = status;
        else if (!busy && _statusLabel.Text.Contains("...", StringComparison.Ordinal))
            _statusLabel.Text = "Ready";
    }

    private void SetStatusFromWorker(string message)
    {
        if (IsDisposed || Disposing)
            return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => _statusLabel.Text = message)); }
            catch (InvalidOperationException) { }
        }
        else
        {
            _statusLabel.Text = message;
        }
    }

    private void AppendLog(string category, string message)
    {
        if (IsDisposed || Disposing)
            return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => AppendLog(category, message))); }
            catch (InvalidOperationException) { }
            return;
        }

        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        Color color = category switch
        {
            "ERROR" => Color.Salmon,
            "WARN" => Color.Khaki,
            "SUCCESS" => Color.LightGreen,
            "CHOICE" => Color.LightSkyBlue,
            _ => Color.Gainsboro
        };
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.SelectionLength = 0;
        _logTextBox.SelectionColor = color;
        _logTextBox.AppendText($"[{timestamp}] [{category}] {message}{Environment.NewLine}");
        _logTextBox.SelectionColor = _logTextBox.ForeColor;
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.ScrollToCaret();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _operationCts?.Cancel();
            _operationCts?.Dispose();
            _maven.Dispose();
        }
        base.Dispose(disposing);
    }
}
