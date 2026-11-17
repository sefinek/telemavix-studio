using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Telemavix.Core.Mapping;
using Telemavix.Core.Telemetry;

namespace Telemavix.Gui;

/// <summary>Every map of the picker's list showing the same place, so they can be compared side by side. A click picks one and closes the window.</summary>
internal sealed class MapComparisonWindow : Window
{
	private const int MinCellWidth = 372;
	private const int CardGap = 12;
	private const int CardChrome = 20;
	private const int MinZoom = 8;
	private const int MaxZoom = 17;
	private const int ParallelFetches = 4;
	private const int DragThreshold = 6;
	private const double StartLat = 54.18318143337505;
	private const double StartLon = 15.599115397435279;

	private sealed class Card(string id, Image image, TextBlock status)
	{
		public string Id { get; } = id;
		public Image Image { get; } = image;
		public TextBlock Status { get; } = status;
		public Bitmap? Bitmap { get; set; }
	}

	private readonly MapSources _sources;
	private readonly Action<string> _pick;
	private readonly SemaphoreSlim _fetches = new(ParallelFetches);
	private readonly List<Card> _cards = [];
	private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 28, TextAlignment = TextAlignment.Center };
	private CancellationTokenSource _loading = new();
	private readonly TextBox _coordinates = new() { Width = 210, VerticalAlignment = VerticalAlignment.Center, PlaceholderText = "54.18318, 15.59911" };
	private double _lat = StartLat;
	private double _lon = StartLon;
	private readonly List<UniformGrid> _grids = [];
	private readonly DispatcherTimer _resized = new() { Interval = TimeSpan.FromMilliseconds(400) };
	private double _pictureWidth = 360;
	private double _pendingPictureWidth;
	private bool _laidOut;
	private int _zoom = 13;
	private Point _pressedAt;
	private bool _pressed;
	private bool _dragging;

	public MapComparisonWindow(MapSources sources, string? currentId, Action<string> pick)
	{
		_sources = sources;
		_pick = pick;
		Title = Strings.MapCompare_Title;
		Width = 1240;
		Height = 820;
		MinWidth = 420;
		MinHeight = 360;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		WindowState = WindowState.Maximized;
		Background = Palette.WindowBackground;
		Icon = AppAssets.Icon;

		var zoomOut = new Button { Content = "-", MinWidth = 36 };
		zoomOut.Click += (_, _) => ChangeZoom(-1);
		var zoomIn = new Button { Content = "+", MinWidth = 36 };
		zoomIn.Click += (_, _) => ChangeZoom(1);
		var zoom = new StackPanel
		{
			Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
			Children = { new TextBlock { Text = Strings.MapCompare_Zoom, Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center }, zoomOut, _zoomText, zoomIn }
		};
		_coordinates.KeyDown += (_, e) =>
		{
			if (e.Key == Key.Enter) ApplyCoordinates();
		};
		_coordinates.LostFocus += (_, _) => ApplyCoordinates();
		var place = new StackPanel
		{
			Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
			Children = { new TextBlock { Text = Strings.MapCompare_Place, Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center }, _coordinates }
		};
		var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, VerticalAlignment = VerticalAlignment.Center, Children = { place, zoom } };
		var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(24, 20, 24, 8), ColumnSpacing = 16 };
		header.Children.Add(new StackPanel
		{
			Spacing = 4,
			Children =
			{
				new TextBlock { Text = Strings.MapCompare_Title, FontSize = 20, FontWeight = FontWeight.Bold },
				new TextBlock { Text = Strings.MapCompare_Intro, Classes = { "hint" }, FontSize = 14, TextWrapping = TextWrapping.Wrap }
			}
		});
		Grid.SetColumn(controls, 1);
		header.Children.Add(controls);

		string[] satellite =
		[
			MapProviders.AutoSatelliteId,
			.. MapProviders.BuiltIn.Where(p => MapSourcePicker.SatelliteIds.Contains(p.Id) && !MapSourcePicker.EsriDuplicates.Contains(p.Id)).Select(p => p.Id)
		];
		string[] streets =
		[
			MapProviders.StreetsAutoId,
			.. MapProviders.BuiltIn.Where(p => !MapSourcePicker.SatelliteIds.Contains(p.Id) && !MapSourcePicker.EsriDuplicates.Contains(p.Id)).Select(p => p.Id)
		];
		var sections = new StackPanel { Spacing = 10, Margin = new Thickness(24, 8, 12, 24) };
		sections.SizeChanged += (_, e) => OnSectionsResized(e.NewSize.Width);
		sections.Children.Add(Section(Strings.MapSource_GroupSatellite, satellite, currentId));
		sections.Children.Add(Section(Strings.MapSource_GroupStreets, streets, currentId));

		var scroll = new ScrollViewer { Content = sections, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
		var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
		root.Children.Add(header);
		Grid.SetRow(scroll, 1);
		root.Children.Add(scroll);
		Content = root;

		ShowPlace();
		_resized.Tick += (_, _) =>
		{
			_resized.Stop();
			_pictureWidth = _pendingPictureWidth;
			Reload();
		};
		AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
		Closed += (_, _) => Release();
	}

	/// <summary>Ctrl and the wheel change the zoom, as on a map; without Ctrl the wheel scrolls the list as it does.</summary>
	private void OnWheel(object? sender, PointerWheelEventArgs e)
	{
		if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;

		ChangeZoom(e.Delta.Y > 0 ? 1 : -1);
		e.Handled = true;
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		if (e.Key == Key.Escape) Close();
	}

	private StackPanel Section(string title, IEnumerable<string> ids, string? currentId)
	{
		var panel = new StackPanel { Spacing = 8 };
		panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 12, 0, 0) });
		var grid = new UniformGrid { Columns = 1 };
		foreach (string id in ids) grid.Children.Add(CardFor(id, id == currentId));
		_grids.Add(grid);
		panel.Children.Add(grid);
		return panel;
	}

	private Border CardFor(string id, bool current)
	{
		MapTerms terms = MapProviders.Get(_sources.Resolve(id)).Terms;
		var image = new Image { Stretch = Stretch.UniformToFill };
		var status = new TextBlock
		{
			Classes = { "hint" }, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(16),
			HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
		};
		_cards.Add(new Card(id, image, status));

		(Geometry icon, IBrush color, string verdict) = terms.Video switch
		{
			MapUse.Allowed => (Icons.Check, Palette.Success, Strings.MapTerms_VideoAllowed),
			MapUse.NotAllowed => (Icons.Warning, Palette.Danger, Strings.MapTerms_VideoNotAllowed),
			MapUse.PaidPlanOnly => (Icons.Warning, Palette.Warning, Strings.MapTerms_VideoPaid),
			MapUse.Conditional => (Icons.Exclamation, Palette.Warning, Strings.MapTerms_VideoConditional),
			_ => (Icons.Info, Palette.TextMuted, Strings.MapTerms_VideoNotStated)
		};
		var verdictRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
		verdictRow.Children.Add(new IconView { Data = icon, Foreground = color, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
		var verdictText = new TextBlock { Text = verdict, Foreground = color, FontSize = 12, TextWrapping = TextWrapping.Wrap };
		Grid.SetColumn(verdictText, 1);
		verdictRow.Children.Add(verdictText);

		var picture = new Panel { ClipToBounds = true, Children = { image, status } };
		picture.SizeChanged += (_, e) =>
		{
			double height = e.NewSize.Width / 2;
			if (double.IsNaN(picture.Height) || Math.Abs(picture.Height - height) > 0.5) picture.Height = height;
		};
		var body = new StackPanel
		{
			Spacing = 6,
			Children = { picture, new TextBlock { Text = _sources.NameOf(id), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, verdictRow }
		};
		var card = new Border
		{
			Padding = new Thickness(8), Margin = new Thickness(0, 0, CardGap, CardGap), Child = body, Cursor = new Cursor(StandardCursorType.Hand),
			BorderThickness = new Thickness(2), BorderBrush = current ? Palette.Accent : Brushes.Transparent
		};
		card.Classes.Add("card");
		card.PointerPressed += OnCardPressed;
		card.PointerMoved += OnCardMoved;
		card.PointerReleased += (_, e) => OnCardReleased(id, e);
		return card;
	}

	/// <summary>A press and release on a card picks its map; dragging instead moves the place of every map by the distance dragged.</summary>
	private void OnCardPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Border card || !e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;

		_pressed = true;
		_dragging = false;
		_pressedAt = e.GetPosition(this);
		e.Pointer.Capture(card);
	}

	private void OnCardMoved(object? sender, PointerEventArgs e)
	{
		if (!_pressed) return;

		Point delta = e.GetPosition(this) - _pressedAt;
		if (!_dragging && Math.Abs(delta.X) + Math.Abs(delta.Y) < DragThreshold) return;

		_dragging = true;
		foreach (Card card in _cards) card.Image.RenderTransform = new TranslateTransform(delta.X, delta.Y);
	}

	private void OnCardReleased(string id, PointerReleasedEventArgs e)
	{
		if (!_pressed) return;

		_pressed = false;
		e.Pointer.Capture(null);
		if (!_dragging)
		{
			_pick(id);
			Close();
			return;
		}

		_dragging = false;
		Point delta = e.GetPosition(this) - _pressedAt;
		foreach (Card card in _cards) card.Image.RenderTransform = null;
		double degreesPerPixel = DegreesPerPixel(_zoom);
		_lon -= delta.X * degreesPerPixel;
		_lat = Math.Clamp(_lat + delta.Y * degreesPerPixel * Math.Cos(AngleMath.DegToRad(_lat)), -85, 85);
		ShowPlace();
		Reload();
	}

	private void ApplyCoordinates()
	{
		if (!TryParsePlace(_coordinates.Text, out double lat, out double lon))
		{
			ShowPlace();
			return;
		}

		if (Math.Abs(lat - _lat) < 1e-6 && Math.Abs(lon - _lon) < 1e-6) return;

		_lat = lat;
		_lon = lon;
		ShowPlace();
		Reload();
	}

	/// <summary>"54.18318, 15.59911" - latitude then longitude, as copied from a map; a comma, semicolon or space between, a dot in the numbers.</summary>
	private static bool TryParsePlace(string? text, out double lat, out double lon)
	{
		lat = lon = 0;
		string[] parts = (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat) &&
		       double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lon) && lat is >= -85 and <= 85 && lon is >= -180 and <= 180;
	}

	private void ShowPlace()
	{
		_coordinates.Text = string.Create(CultureInfo.InvariantCulture, $"{_lat:0.000000}, {_lon:0.000000}");
	}

	private void ChangeZoom(int step)
	{
		int zoom = Math.Clamp(_zoom + step, MinZoom, MaxZoom);
		if (zoom == _zoom) return;

		_zoom = zoom;
		Reload();
	}

	private void Reload()
	{
		_loading.Cancel();
		_loading = new CancellationTokenSource();
		_zoomText.Text = _zoom.ToString();
		foreach (Card card in _cards) _ = LoadAsync(card, _zoom, _loading.Token);
	}

	private async Task LoadAsync(Card card, int zoom, CancellationToken ct)
	{
		card.Image.Source = null;
		card.Bitmap?.Dispose();
		card.Bitmap = null;
		if (_sources.MissesApiKey(card.Id))
		{
			card.Status.Text = Strings.MapSource_ApiKeyRequired;
			return;
		}

		card.Status.Text = Strings.Welcome_MapPreviewLoading;
		Bitmap? bitmap;
		try
		{
			await _fetches.WaitAsync(ct);
			try
			{
				bitmap = await MapPreview.RenderAsync(Area(zoom), _sources.UrlTemplate(card.Id), zoom, _sources.CacheMaxAge(card.Id), _sources.LabelsUrlTemplate(card.Id), ct);
			}
			finally
			{
				_fetches.Release();
			}
		}
		catch (OperationCanceledException)
		{
			return;
		}

		if (ct.IsCancellationRequested)
		{
			bitmap?.Dispose();
			return;
		}

		card.Bitmap = bitmap;
		card.Image.Source = bitmap;
		card.Status.Text = bitmap is null ? Strings.Welcome_MapPreviewFailed : "";
	}

	/// <summary>
	///     The cards fill the window's width: as many columns as fit, each as wide as the room allows. The pictures are fetched for that size (a
	///     view of exactly a card's width in world pixels, so a drag in pixels is a drag on the map); a resize fetches them again once it settles.
	/// </summary>
	private void OnSectionsResized(double width)
	{
		if (width <= 0) return;

		int columns = Math.Max(1, (int)(width / MinCellWidth));
		double picture = Math.Floor(width / columns) - CardGap - CardChrome;
		foreach (UniformGrid grid in _grids) grid.Columns = columns;
		if (!_laidOut)
		{
			_laidOut = true;
			_pictureWidth = picture;
			Reload();
			return;
		}

		if (Math.Abs(picture - _pictureWidth) < 1) return;

		_pendingPictureWidth = picture;
		_resized.Stop();
		_resized.Start();
	}

	/// <summary>The same stretch of the world for every map: a card's picture at this zoom (the mosaic's view is the area plus its margin, hence the division), the height shrunk by the projection's stretch at the latitude.</summary>
	private (double Lat, double Lon)[] Area(int zoom)
	{
		double degreesPerPixel = DegreesPerPixel(zoom);
		double halfLon = _pictureWidth / RouteMapMosaic.ViewMargin / 2 * degreesPerPixel;
		double halfLat = _pictureWidth / 2 / RouteMapMosaic.ViewMargin / 2 * degreesPerPixel * Math.Cos(AngleMath.DegToRad(_lat));
		return [(_lat - halfLat, _lon - halfLon), (_lat + halfLat, _lon + halfLon)];
	}

	private static double DegreesPerPixel(int zoom)
	{
		return 360.0 / (256.0 * Math.Pow(2, zoom));
	}

	private void Release()
	{
		_resized.Stop();
		_loading.Cancel();
		foreach (Card card in _cards)
		{
			card.Image.Source = null;
			card.Bitmap?.Dispose();
			card.Bitmap = null;
		}
	}
}
