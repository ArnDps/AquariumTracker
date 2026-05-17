using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.WriteIndented = true;
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.WebHost.UseUrls("http://localhost:5050");

var app = builder.Build();

var dataFile = Path.Combine(AppContext.BaseDirectory, "aquarium-data.json");
var dataLock = new SemaphoreSlim(1, 1);
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

app.MapGet("/", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(HtmlPage);
});

app.MapGet("/api/data", async () =>
{
    await dataLock.WaitAsync();
    try
    {
        if (!File.Exists(dataFile))
        {
            var initial = AquariumAppData.CreateDefault();
            await File.WriteAllTextAsync(dataFile, JsonSerializer.Serialize(initial, jsonOptions));
            return Results.Json(initial, jsonOptions);
        }

        var json = await File.ReadAllTextAsync(dataFile);
        var data = JsonSerializer.Deserialize<AquariumAppData>(json, jsonOptions) ?? AquariumAppData.CreateDefault();
        return Results.Json(data, jsonOptions);
    }
    finally
    {
        dataLock.Release();
    }
});

app.MapPost("/api/data", async (AquariumAppData incoming) =>
{
    if (incoming.Aquariums.Count == 0)
    {
        incoming.Aquariums.Add(new AquariumItem { Id = "bac-principal", Name = "Bac principal" });
    }

    await dataLock.WaitAsync();
    try
    {
        var json = JsonSerializer.Serialize(incoming, jsonOptions);
        await File.WriteAllTextAsync(dataFile, json, Encoding.UTF8);
        return Results.Ok(new { success = true });
    }
    finally
    {
        dataLock.Release();
    }
});

app.Run();

internal sealed class AquariumAppData
{
    public List<AquariumItem> Aquariums { get; set; } = new();
    public List<MeasurementEntry> Entries { get; set; } = new();
    public List<MaintenanceEntry> Maintenance { get; set; } = new();
    public TargetSettings Targets { get; set; } = new();

    public static AquariumAppData CreateDefault() => new()
    {
        Aquariums = new List<AquariumItem>
        {
            new() { Id = "bac-principal", Name = "Bac principal" }
        },
        Entries = new List<MeasurementEntry>(),
        Maintenance = new List<MaintenanceEntry>(),
        Targets = new TargetSettings()
    };
}

internal sealed class AquariumItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

internal sealed class MeasurementEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AquariumId { get; set; } = "bac-principal";
    public string MeasuredAt { get; set; } = DateTime.Now.ToString("yyyy-MM-ddTHH:mm");
    public decimal? Temperature { get; set; }
    public decimal? Ph { get; set; }
    public decimal? Kh { get; set; }
    public decimal? Gh { get; set; }
    public decimal? No2 { get; set; }
    public decimal? No3 { get; set; }
    public decimal? Co2 { get; set; }
    public decimal? Nh4 { get; set; }
    public decimal? Conductivity { get; set; }
    public string? Notes { get; set; }
}

internal sealed class MaintenanceEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AquariumId { get; set; } = "bac-principal";
    public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-ddTHH:mm");
    public string Type { get; set; } = "changement_eau";
    public string? Details { get; set; }
}

internal sealed class TargetSettings
{
    public decimal TemperatureMin { get; set; } = 24.0m;
    public decimal TemperatureMax { get; set; } = 26.0m;
    public decimal PhMin { get; set; } = 6.5m;
    public decimal PhMax { get; set; } = 7.2m;
    public decimal No2Max { get; set; } = 0.05m;
    public decimal No3Max { get; set; } = 20.0m;
}

const string HtmlPage = @"""
<!DOCTYPE html>
<html lang="fr">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Aquarium Tracker C#</title>
  <script src="https://cdn.jsdelivr.net/npm/chart.js"></script>
  <style>
    :root {
      --bg: #f8fafc;
      --card: #ffffff;
      --text: #0f172a;
      --muted: #64748b;
      --border: #e2e8f0;
      --primary: #0f766e;
      --danger: #b91c1c;
      --warn: #a16207;
      --ok: #166534;
      --shadow: 0 8px 24px rgba(15, 23, 42, 0.06);
      --radius: 18px;
    }

    * { box-sizing: border-box; }
    body {
      margin: 0;
      font-family: Inter, Arial, sans-serif;
      color: var(--text);
      background: var(--bg);
    }

    .container {
      max-width: 1280px;
      margin: 0 auto;
      padding: 24px;
    }

    .grid {
      display: grid;
      gap: 16px;
    }

    .grid-hero {
      grid-template-columns: 1.3fr 0.7fr;
    }

    .grid-2 {
      grid-template-columns: 1fr 1fr;
    }

    .grid-6 {
      grid-template-columns: repeat(6, 1fr);
    }

    .card {
      background: var(--card);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      box-shadow: var(--shadow);
      padding: 20px;
    }

    h1, h2, h3, p { margin-top: 0; }
    .muted { color: var(--muted); }
    .actions, .row { display: flex; gap: 10px; flex-wrap: wrap; }
    .row-between { display: flex; justify-content: space-between; align-items: center; gap: 12px; }

    button {
      border: 0;
      border-radius: 14px;
      background: var(--primary);
      color: white;
      padding: 10px 14px;
      cursor: pointer;
      font-weight: 600;
    }

    button.secondary {
      background: white;
      color: var(--text);
      border: 1px solid var(--border);
    }

    input, select, textarea {
      width: 100%;
      border: 1px solid var(--border);
      border-radius: 12px;
      padding: 10px 12px;
      font: inherit;
      background: white;
    }

    textarea { min-height: 110px; resize: vertical; }
    label { font-size: 14px; font-weight: 600; display: block; margin-bottom: 6px; }
    .field { margin-bottom: 14px; }
    .tabs { display: flex; gap: 10px; flex-wrap: wrap; margin-bottom: 18px; }
    .tab { background: white; color: var(--text); border: 1px solid var(--border); }
    .tab.active { background: var(--primary); color: white; }
    .hidden { display: none; }

    .badge {
      padding: 6px 10px;
      border-radius: 999px;
      font-size: 12px;
      font-weight: 700;
      display: inline-block;
      border: 1px solid transparent;
    }

    .badge.ok { background: #dcfce7; color: var(--ok); }
    .badge.warn { background: #fef3c7; color: var(--warn); }
    .badge.alert { background: #fee2e2; color: var(--danger); }
    .badge.outline { background: white; color: var(--text); border-color: var(--border); }

    .summary {
      font-size: 28px;
      font-weight: 700;
      margin-top: 8px;
    }

    table {
      width: 100%;
      border-collapse: collapse;
      font-size: 14px;
    }

    th, td {
      padding: 10px 8px;
      border-bottom: 1px solid var(--border);
      text-align: left;
      vertical-align: top;
    }

    .alert-box {
      border-radius: 14px;
      padding: 12px;
      font-size: 14px;
    }

    .alert-ok { background: #ecfdf5; color: var(--ok); border: 1px solid #bbf7d0; }
    .alert-warn { background: #fffbeb; color: var(--warn); border: 1px solid #fde68a; }

    .maintenance-item {
      border: 1px solid var(--border);
      border-radius: 14px;
      padding: 14px;
      margin-bottom: 12px;
      background: white;
    }

    .small { font-size: 13px; }

    @media (max-width: 1100px) {
      .grid-6 { grid-template-columns: repeat(3, 1fr); }
    }

    @media (max-width: 900px) {
      .grid-hero, .grid-2 { grid-template-columns: 1fr; }
    }

    @media (max-width: 700px) {
      .grid-6 { grid-template-columns: repeat(2, 1fr); }
      .container { padding: 14px; }
      table, thead, tbody, th, td, tr { display: block; }
      thead { display: none; }
      tr {
        border: 1px solid var(--border);
        border-radius: 14px;
        padding: 10px;
        margin-bottom: 12px;
        background: white;
      }
      td { border: 0; padding: 6px 0; }
    }
  </style>
</head>
<body>
  <div class="container">
    <div class="grid grid-hero">
      <div class="card">
        <h1>Suivi des paramètres d'aquarium</h1>
        <p class="muted">Application web responsive en C# pour saisir, historiser et visualiser les mesures de tes aquariums.</p>
        <div class="actions">
          <button type="button" onclick="exportJson()">Exporter JSON</button>
          <button type="button" class="secondary" onclick="exportCsv()">Exporter CSV</button>
          <button type="button" class="secondary" onclick="document.getElementById('importFile').click()">Importer JSON</button>
          <input id="importFile" type="file" accept="application/json" class="hidden" onchange="importJson(event)" />
          <button type="button" onclick="saveAll()">Enregistrer</button>
        </div>
      </div>
      <div class="card">
        <div class="row-between">
          <div>
            <div class="muted small">Dernière mesure</div>
            <div id="latestDate">Aucune</div>
          </div>
          <span id="latestBadge" class="badge outline">En attente</span>
        </div>
        <div id="latestAlert" class="alert-box alert-ok" style="margin-top: 12px;">Aucune mesure enregistrée.</div>
      </div>
    </div>

    <div id="summaryCards" class="grid grid-6" style="margin-top: 16px;"></div>

    <div class="tabs" style="margin-top: 20px;">
      <button class="tab active" onclick="showTab('saisie', this)">Saisie</button>
      <button class="tab" onclick="showTab('historique', this)">Historique</button>
      <button class="tab" onclick="showTab('graphiques', this)">Graphiques</button>
      <button class="tab" onclick="showTab('entretien', this)">Entretien</button>
    </div>

    <section id="tab-saisie">
      <div class="grid grid-2">
        <div class="card">
          <h2>Ajouter une mesure</h2>
          <form onsubmit="addEntry(event)">
            <div class="field">
              <label for="aquariumId">Aquarium</label>
              <select id="aquariumId"></select>
            </div>
            <div class="field">
              <label for="measuredAt">Date et heure</label>
              <input id="measuredAt" type="datetime-local" />
            </div>
            <div class="grid grid-2">
              <div class="field"><label>Température (°C)</label><input id="temperature" type="number" step="0.1" inputmode="decimal" /></div>
              <div class="field"><label>pH</label><input id="ph" type="number" step="0.01" inputmode="decimal" /></div>
              <div class="field"><label>KH</label><input id="kh" type="number" step="0.1" inputmode="decimal" /></div>
              <div class="field"><label>GH</label><input id="gh" type="number" step="0.1" inputmode="decimal" /></div>
              <div class="field"><label>NO2 (mg/L)</label><input id="no2" type="number" step="0.01" inputmode="decimal" /></div>
              <div class="field"><label>NO3 (mg/L)</label><input id="no3" type="number" step="0.1" inputmode="decimal" /></div>
              <div class="field"><label>CO2 (ppm)</label><input id="co2" type="number" step="0.1" inputmode="decimal" /></div>
              <div class="field"><label>NH4 (mg/L)</label><input id="nh4" type="number" step="0.01" inputmode="decimal" /></div>
              <div class="field"><label>Conductivité (µS/cm)</label><input id="conductivity" type="number" step="0.1" inputmode="decimal" /></div>
            </div>
            <div class="field">
              <label for="notes">Notes</label>
              <textarea id="notes" placeholder="Entretien, changement d'eau, engrais, comportement des poissons..."></textarea>
            </div>
            <button type="submit">Ajouter la mesure</button>
          </form>
        </div>

        <div class="card">
          <h2>Gestion des bacs et seuils</h2>
          <div class="row">
            <input id="newAquariumName" placeholder="Ex. Aquarium salon 240L" />
            <button type="button" onclick="addAquarium()">Ajouter</button>
          </div>
          <div id="aquariumList" style="margin-top: 16px;"></div>

          <h3 style="margin-top: 22px;">Seuils cibles</h3>
          <div class="grid grid-2">
            <div class="field"><label>Température min</label><input id="temperatureMin" type="number" step="0.1" inputmode="decimal" /></div>
            <div class="field"><label>Température max</label><input id="temperatureMax" type="number" step="0.1" inputmode="decimal" /></div>
            <div class="field"><label>pH min</label><input id="phMin" type="number" step="0.01" inputmode="decimal" /></div>
            <div class="field"><label>pH max</label><input id="phMax" type="number" step="0.01" inputmode="decimal" /></div>
            <div class="field"><label>NO2 max</label><input id="no2Max" type="number" step="0.01" inputmode="decimal" /></div>
            <div class="field"><label>NO3 max</label><input id="no3Max" type="number" step="0.1" inputmode="decimal" /></div>
          </div>
        </div>
      </div>
    </section>

    <section id="tab-historique" class="hidden">
      <div class="card">
        <div class="row-between">
          <h2>Historique des mesures</h2>
          <select id="historyFilter" onchange="renderAll()"></select>
        </div>
        <div style="overflow-x: auto; margin-top: 12px;">
          <table>
            <thead>
              <tr>
                <th>Date</th>
                <th>Bac</th>
                <th>Temp.</th>
                <th>pH</th>
                <th>KH</th>
                <th>GH</th>
                <th>NO2</th>
                <th>NO3</th>
                <th>CO2</th>
                <th>État</th>
                <th></th>
              </tr>
            </thead>
            <tbody id="historyBody"></tbody>
          </table>
        </div>
      </div>
    </section>

    <section id="tab-graphiques" class="hidden">
      <div class="card">
        <div class="row-between">
          <h2>Évolution des paramètres</h2>
          <div class="row">
            <select id="chartFilter" onchange="renderChart()"></select>
            <select id="chartMetric" onchange="renderChart()">
              <option value="temperature">Température</option>
              <option value="ph">pH</option>
              <option value="kh">KH</option>
              <option value="gh">GH</option>
              <option value="no2">NO2</option>
              <option value="no3">NO3</option>
              <option value="co2">CO2</option>
              <option value="nh4">NH4</option>
              <option value="conductivity">Conductivité</option>
            </select>
          </div>
        </div>
        <div style="height: 380px; margin-top: 16px;">
          <canvas id="chartCanvas"></canvas>
        </div>
      </div>
    </section>

    <section id="tab-entretien" class="hidden">
      <div class="grid grid-2">
        <div class="card">
          <h2>Journal d'entretien</h2>
          <form onsubmit="addMaintenance(event)">
            <div class="field"><label>Aquarium</label><select id="maintenanceAquariumId"></select></div>
            <div class="field"><label>Date et heure</label><input id="maintenanceDate" type="datetime-local" /></div>
            <div class="field">
              <label>Type</label>
              <select id="maintenanceType">
                <option value="changement_eau">Changement d'eau</option>
                <option value="nettoyage_filtre">Nettoyage filtre</option>
                <option value="taille_plantes">Taille des plantes</option>
                <option value="fertilisation">Fertilisation</option>
                <option value="ajout_population">Ajout de population</option>
                <option value="autre">Autre</option>
              </select>
            </div>
            <div class="field"><label>Détails</label><textarea id="maintenanceDetails"></textarea></div>
            <button type="submit">Ajouter au journal</button>
          </form>
        </div>

        <div class="card">
          <div class="row-between">
            <h2>Historique entretien</h2>
            <select id="maintenanceFilter" onchange="renderMaintenance()"></select>
          </div>
          <div id="maintenanceList" style="margin-top: 12px;"></div>
        </div>
      </div>
    </section>
  </div>

  <script>
    let appData = null;
    let chart = null;

    function nowLocal() {
      const d = new Date();
      const pad = (n) => String(n).padStart(2, '0');
      return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
    }

    function formatDateTime(value) {
      if (!value) return '—';
      const d = new Date(value);
      if (Number.isNaN(d.getTime())) return value;
      return d.toLocaleString('fr-FR');
    }

    function parseDecimal(value) {
      if (value === null || value === undefined || value === '') return null;
      const normalized = String(value).replace(',', '.');
      const parsed = Number(normalized);
      return Number.isFinite(parsed) ? parsed : null;
    }

    function statusFor(entry) {
      const t = appData.targets;
      const alerts = [];
      if (entry.temperature != null && (entry.temperature < t.temperatureMin || entry.temperature > t.temperatureMax)) alerts.push('Température à surveiller');
      if (entry.ph != null && (entry.ph < t.phMin || entry.ph > t.phMax)) alerts.push('pH atypique');
      if (entry.no2 != null && entry.no2 > t.no2Max) alerts.push('Nitrites élevés');
      if (entry.no3 != null && entry.no3 > t.no3Max) alerts.push('Nitrates élevés');
      if (alerts.length === 0) return { label: 'Stable', css: 'ok', alerts };
      if (alerts.length <= 2) return { label: 'Vigilance', css: 'warn', alerts };
      return { label: 'Alerte', css: 'alert', alerts };
    }

    function aquariumName(id) {
      const found = appData.aquariums.find(a => a.id === id);
      return found ? found.name : id;
    }

    async function loadData() {
      const res = await fetch('/api/data');
      appData = await res.json();
      renderAll();
    }

    async function saveAll() {
      readTargets();
      const res = await fetch('/api/data', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(appData)
      });

      if (res.ok) {
        alert('Données enregistrées.');
      } else {
        alert('Erreur lors de l\'enregistrement.');
      }
    }

    function exportJson() {
      const blob = new Blob([JSON.stringify(appData, null, 2)], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'aquarium-data.json';
      a.click();
      URL.revokeObjectURL(url);
    }

    function exportCsv() {
      const headers = ['Aquarium', 'Date', 'Temperature', 'pH', 'KH', 'GH', 'NO2', 'NO3', 'CO2', 'NH4', 'Conductivite', 'Notes'];
      const rows = [...appData.entries]
        .sort((a, b) => new Date(b.measuredAt) - new Date(a.measuredAt))
        .map(e => [aquariumName(e.aquariumId), e.measuredAt, e.temperature ?? '', e.ph ?? '', e.kh ?? '', e.gh ?? '', e.no2 ?? '', e.no3 ?? '', e.co2 ?? '', e.nh4 ?? '', e.conductivity ?? '', (e.notes || '').replace(/\n/g, ' ')]);
      const csv = [headers, ...rows].map(r => r.map(c => `"${String(c).replaceAll('"', '""')}"`).join(';')).join('\n');
      const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'aquarium-data.csv';
      a.click();
      URL.revokeObjectURL(url);
    }

    function importJson(event) {
      const file = event.target.files?.[0];
      if (!file) return;
      const reader = new FileReader();
      reader.onload = async () => {
        try {
          appData = JSON.parse(reader.result);
          renderAll();
          await saveAll();
        } catch {
          alert('Fichier invalide.');
        }
      };
      reader.readAsText(file);
    }

    function showTab(name, button) {
      document.querySelectorAll('section[id^="tab-"]').forEach(x => x.classList.add('hidden'));
      document.getElementById(`tab-${name}`).classList.remove('hidden');
      document.querySelectorAll('.tab').forEach(x => x.classList.remove('active'));
      button.classList.add('active');
      if (name === 'graphiques') renderChart();
    }

    function addAquarium() {
      const input = document.getElementById('newAquariumName');
      const name = input.value.trim();
      if (!name) return;
      const id = name.toLowerCase().normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/[^a-z0-9]+/g, '-');
      if (appData.aquariums.some(a => a.id === id)) return;
      appData.aquariums.push({ id, name });
      input.value = '';
      renderAll();
    }

    function addEntry(event) {
      event.preventDefault();
      const entry = {
        id: crypto.randomUUID(),
        aquariumId: document.getElementById('aquariumId').value,
        measuredAt: document.getElementById('measuredAt').value,
        temperature: parseDecimal(document.getElementById('temperature').value),
        ph: parseDecimal(document.getElementById('ph').value),
        kh: parseDecimal(document.getElementById('kh').value),
        gh: parseDecimal(document.getElementById('gh').value),
        no2: parseDecimal(document.getElementById('no2').value),
        no3: parseDecimal(document.getElementById('no3').value),
        co2: parseDecimal(document.getElementById('co2').value),
        nh4: parseDecimal(document.getElementById('nh4').value),
        conductivity: parseDecimal(document.getElementById('conductivity').value),
        notes: document.getElementById('notes').value.trim() || null
      };
      appData.entries.push(entry);
      event.target.reset();
      document.getElementById('measuredAt').value = nowLocal();
      document.getElementById('aquariumId').value = entry.aquariumId;
      renderAll();
    }

    function addMaintenance(event) {
      event.preventDefault();
      const item = {
        id: crypto.randomUUID(),
        aquariumId: document.getElementById('maintenanceAquariumId').value,
        date: document.getElementById('maintenanceDate').value,
        type: document.getElementById('maintenanceType').value,
        details: document.getElementById('maintenanceDetails').value.trim() || null
      };
      appData.maintenance.push(item);
      event.target.reset();
      document.getElementById('maintenanceDate').value = nowLocal();
      document.getElementById('maintenanceAquariumId').value = item.aquariumId;
      renderMaintenance();
    }

    function deleteEntry(id) {
      appData.entries = appData.entries.filter(x => x.id !== id);
      renderAll();
    }

    function readTargets() {
      appData.targets.temperatureMin = parseDecimal(document.getElementById('temperatureMin').value) ?? 24;
      appData.targets.temperatureMax = parseDecimal(document.getElementById('temperatureMax').value) ?? 26;
      appData.targets.phMin = parseDecimal(document.getElementById('phMin').value) ?? 6.5;
      appData.targets.phMax = parseDecimal(document.getElementById('phMax').value) ?? 7.2;
      appData.targets.no2Max = parseDecimal(document.getElementById('no2Max').value) ?? 0.05;
      appData.targets.no3Max = parseDecimal(document.getElementById('no3Max').value) ?? 20;
    }

    function bindFilters() {
      const options = ['<option value="all">Tous les aquariums</option>']
        .concat(appData.aquariums.map(a => `<option value="${a.id}">${a.name}</option>`))
        .join('');

      ['historyFilter', 'chartFilter', 'maintenanceFilter'].forEach(id => {
        const el = document.getElementById(id);
        const current = el.value;
        el.innerHTML = options;
        if ([...el.options].some(o => o.value === current)) el.value = current;
      });

      const aquariumOptions = appData.aquariums.map(a => `<option value="${a.id}">${a.name}</option>`).join('');
      ['aquariumId', 'maintenanceAquariumId'].forEach(id => {
        const el = document.getElementById(id);
        const current = el.value;
        el.innerHTML = aquariumOptions;
        if ([...el.options].some(o => o.value === current)) el.value = current;
      });
    }

    function renderAquariumList() {
      const container = document.getElementById('aquariumList');
      container.innerHTML = appData.aquariums.map(a => {
        const count = appData.entries.filter(e => e.aquariumId === a.id).length;
        return `<div class="maintenance-item"><div class="row-between"><div><strong>${a.name}</strong><div class="muted small">${a.id}</div></div><span class="badge outline">${count} mesures</span></div></div>`;
      }).join('');
    }

    function renderTargets() {
      document.getElementById('temperatureMin').value = appData.targets.temperatureMin;
      document.getElementById('temperatureMax').value = appData.targets.temperatureMax;
      document.getElementById('phMin').value = appData.targets.phMin;
      document.getElementById('phMax').value = appData.targets.phMax;
      document.getElementById('no2Max').value = appData.targets.no2Max;
      document.getElementById('no3Max').value = appData.targets.no3Max;
    }

    function filteredEntries() {
      const filter = document.getElementById('historyFilter').value || 'all';
      return [...appData.entries]
        .filter(e => filter === 'all' || e.aquariumId === filter)
        .sort((a, b) => new Date(b.measuredAt) - new Date(a.measuredAt));
    }

    function renderHeader() {
      const latest = [...appData.entries].sort((a, b) => new Date(b.measuredAt) - new Date(a.measuredAt))[0];
      const summary = document.getElementById('summaryCards');
      if (!latest) {
        document.getElementById('latestDate').textContent = 'Aucune';
        document.getElementById('latestBadge').className = 'badge outline';
        document.getElementById('latestBadge').textContent = 'En attente';
        document.getElementById('latestAlert').className = 'alert-box alert-ok';
        document.getElementById('latestAlert').textContent = 'Aucune mesure enregistrée.';
        summary.innerHTML = '';
        return;
      }

      const status = statusFor(latest);
      document.getElementById('latestDate').textContent = formatDateTime(latest.measuredAt);
      document.getElementById('latestBadge').className = `badge ${status.css}`;
      document.getElementById('latestBadge').textContent = status.label;
      document.getElementById('latestAlert').className = `alert-box ${status.alerts.length ? 'alert-warn' : 'alert-ok'}`;
      document.getElementById('latestAlert').textContent = status.alerts.length ? status.alerts.join(' • ') : 'Aucun indicateur critique détecté.';

      const cards = [
        ['Température', latest.temperature != null ? `${latest.temperature} °C` : '—'],
        ['pH', latest.ph ?? '—'],
        ['NO2', latest.no2 != null ? `${latest.no2} mg/L` : '—'],
        ['NO3', latest.no3 != null ? `${latest.no3} mg/L` : '—'],
        ['CO2', latest.co2 != null ? `${latest.co2} ppm` : '—'],
        ['GH / KH', `${latest.gh ?? '—'} / ${latest.kh ?? '—'}`],
      ];

      summary.innerHTML = cards.map(c => `<div class="card"><div class="muted small">${c[0]}</div><div class="summary">${c[1]}</div></div>`).join('');
    }

    function renderHistory() {
      const body = document.getElementById('historyBody');
      const list = filteredEntries();
      if (list.length === 0) {
        body.innerHTML = '<tr><td colspan="11">Aucune mesure disponible.</td></tr>';
        return;
      }

      body.innerHTML = list.map(entry => {
        const status = statusFor(entry);
        return `<tr>
          <td>${formatDateTime(entry.measuredAt)}</td>
          <td>${aquariumName(entry.aquariumId)}</td>
          <td>${entry.temperature ?? '—'}</td>
          <td>${entry.ph ?? '—'}</td>
          <td>${entry.kh ?? '—'}</td>
          <td>${entry.gh ?? '—'}</td>
          <td>${entry.no2 ?? '—'}</td>
          <td>${entry.no3 ?? '—'}</td>
          <td>${entry.co2 ?? '—'}</td>
          <td><span class="badge ${status.css}">${status.label}</span></td>
          <td><button type="button" class="secondary" onclick="deleteEntry('${entry.id}')">Supprimer</button></td>
        </tr>`;
      }).join('');
    }

    function renderChart() {
      const filter = document.getElementById('chartFilter').value || 'all';
      const metric = document.getElementById('chartMetric').value || 'temperature';
      const list = [...appData.entries]
        .filter(e => filter === 'all' || e.aquariumId === filter)
        .sort((a, b) => new Date(a.measuredAt) - new Date(b.measuredAt))
        .filter(e => e[metric] != null);

      const ctx = document.getElementById('chartCanvas');
      if (chart) chart.destroy();

      chart = new Chart(ctx, {
        type: 'line',
        data: {
          labels: list.map(e => new Date(e.measuredAt).toLocaleDateString('fr-FR')),
          datasets: [{
            label: metric,
            data: list.map(e => e[metric]),
            tension: 0.25,
            borderWidth: 2,
            fill: false
          }]
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: { display: true }
          }
        }
      });
    }

    function renderMaintenance() {
      const filter = document.getElementById('maintenanceFilter').value || 'all';
      const list = [...appData.maintenance]
        .filter(e => filter === 'all' || e.aquariumId === filter)
        .sort((a, b) => new Date(b.date) - new Date(a.date));

      const container = document.getElementById('maintenanceList');
      if (list.length === 0) {
        container.innerHTML = '<div class="alert-box alert-ok">Aucun entretien enregistré.</div>';
        return;
      }

      container.innerHTML = list.map(item => `
        <div class="maintenance-item">
          <div class="row-between">
            <div>
              <strong>${aquariumName(item.aquariumId)}</strong>
              <div class="muted small">${formatDateTime(item.date)}</div>
            </div>
            <span class="badge outline">${item.type.replaceAll('_', ' ')}</span>
          </div>
          <div style="margin-top: 10px;">${item.details || '—'}</div>
        </div>`).join('');
    }

    function renderAll() {
      bindFilters();
      renderAquariumList();
      renderTargets();
      renderHeader();
      renderHistory();
      renderMaintenance();
      if (!document.getElementById('measuredAt').value) document.getElementById('measuredAt').value = nowLocal();
      if (!document.getElementById('maintenanceDate').value) document.getElementById('maintenanceDate').value = nowLocal();
      renderChart();
    }

    window.addEventListener('load', loadData);
  </script>
</body>
</html>
""";
