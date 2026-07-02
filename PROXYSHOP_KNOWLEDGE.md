# Proxyshop — Photoshop Automation Knowledge Base

Reference document for understanding how Proxyshop works so new integrations can be built correctly.  
Source: `C:\Users\vinic\Documents\Magic the Gathering\Proxyshop-v1.13.2`

---

## Overview

Proxyshop is a **Python → Photoshop automation bridge** that renders high-quality Magic: The Gathering proxy cards.

```
art/Damnation.jpg
  → Proxyshop reads filename
  → Fetches card data from Scryfall API
  → Determines correct PSD template
  → Opens .psd in Photoshop via photoshop-python-api
  → Python class populates text layers, enables colour layers, places art
  → Runs optional art_action (e.g., sketch effect)
  → Saves render to out/
```

**Tech stack:** Python 3.9–3.11 + `photoshop-python-api` + Photoshop CC 2017+, Windows only.

---

## Directory Structure

```
Proxyshop-v1.13.2/
├── art/                    ← Input card art images (jpg/png/webp/tif)
├── fonts/                  ← Required MTG fonts (must be installed system-wide)
├── out/                    ← Rendered card output
├── logs/                   ← error.txt for debugging
├── src/
│   ├── data/
│   │   ├── manifest.yml    ← Core template registry (PSD → Python class)
│   │   └── config/         ← Global settings
│   ├── enums/
│   │   └── layers.py       ← Standardized layer name constants (LAYERS.*)
│   ├── helpers.py          ← High-level Photoshop helper functions (psd.*)
│   └── templates/          ← Base template classes
├── templates/              ← Core .psd template files
└── plugins/
    └── PluginName/
        ├── manifest.yml    ← Plugin template registry
        ├── templates/      ← Plugin-specific .psd files
        ├── config/         ← Per-template .toml settings files
        └── py/
            ├── __init__.py
            ├── templates.py       ← Python template classes
            └── actions/           ← Optional art post-processing scripts
                └── sketch.py
```

---

## Templates — `.psd` Files

### What they contain
Each PSD is a **structured, named-layer document**. Layers are organized into groups named after their function. The Python scripts navigate this structure by name, not by position.

### Layer naming
Layer names follow a **standardized scheme** defined in `src/enums/layers.py`:

| Constant | Layer Purpose |
|---|---|
| `LAYERS.BACKGROUND` | Card background (colour-coded variants) |
| `LAYERS.PINLINES_TEXTBOX` | Pinlines + rules text box area |
| `LAYERS.LAND_PINLINES_TEXTBOX` | Pinlines variant for land cards |
| `LAYERS.PT_BOX` | Power/Toughness box |
| `LAYERS.ARTIST` | Artist credit text layer |
| `LAYERS.TRANSFORM` | Transform symbol/indicator group |

Colour variants of a layer are stored as **sibling layers inside a group**, named by colour string (e.g., `"W"`, `"U"`, `"B"`, `"R"`, `"G"`, `"WU"`, `"Artifact"`, `"Land"`). The Python class picks the right one using `psd.getLayer(self.pinlines, LAYERS.PINLINES_TEXTBOX)`.

### Visibility rules
- **Do NOT delete layers** you don't want — the script may forcibly show/hide layers.
- **Do NOT disable visibility** — same reason.
- To permanently hide a layer: **set its Opacity to 0**. This is the only safe method.

---

## `manifest.yml` — Template Registry

The manifest is the **glue between a `.psd` file and a Python class**. There is one in `src/data/` (core) and one per plugin in `plugins/<Name>/`.

### Format
```yaml
<psd_filename>:
  id: '<google_drive_file_id>'   # used by the in-app updater to download the PSD
  desc: 'Human-readable description'
  templates:
    <Template Type Name>: <PythonClassName>
```

### With card-type variants
Some Python classes handle multiple card types. Variants are listed as a sequence:
```yaml
borderless-vector.psd:
  templates:
    Borderless:
      BorderlessVectorTemplate:
      - normal
      - transform_front
      - transform_back
      - mdfc_front
      - mdfc_back
```

### Template type names
These correspond to the **tabs in the GUI** (Normal, MDFC, Transform, etc.).  
Selecting a template in the "Normal" tab **only** affects Normal-type cards.

| Type name | Card category |
|---|---|
| `Normal` | Standard M15-era creature/spell |
| `Fullart` | Fullart treatment |
| `Borderless` | Borderless box-topper |
| `Etched` | Commander Legends etched foil |
| `Chilli Token` | Token cards |

---

## Python Template Classes

### Location
- Core classes: `src/templates/` (inside the bundled src package)
- Plugin classes: `plugins/<Name>/py/templates.py`

### Inheritance hierarchy
Proxyshop uses **Python multiple inheritance as a mixin system**:

```
M15Template          ← base class for modern-frame cards
NormalTemplate       ← alias / variant of M15Template
  ├── ExtendedMod    ← mixin: adds extended art border treatment
  ├── MDFCMod        ← mixin: adds Modal Double-Faced Card support
  └── TransformMod   ← mixin: adds Transform/flip card support
```

**MRO (Method Resolution Order):** Python resolves methods left-to-right through base classes. The leftmost mixin wins.

```python
class SilvanExtendedTemplate(ExtendedMod, M15Template):
    # ExtendedMod methods override M15Template methods when both exist
```

### Key imports
```python
import photoshop.api as ps           # raw Photoshop COM API
import src.helpers as psd            # high-level helpers
from src import CFG, ENV, APP        # app globals (config, env flags, PS application)
from src.enums.layers import LAYERS  # layer name constants
from src.templates import (
    NormalTemplate, M15Template,
    ExtendedMod, MDFCMod, TransformMod
)
```

### Template class properties
Override these `@cached_property` / `@property` methods to customise layer resolution:

```python
@cached_property
def background_layer(self) -> Optional[ArtLayer]:
    """Return the correct background ArtLayer for this card."""
    if self.background == LAYERS.COLORLESS:
        return None            # no background for colorless
    return super().background_layer

@cached_property
def pinlines_layer(self) -> Optional[ArtLayer]:
    if self.is_land:
        return psd.getLayer(self.pinlines, LAYERS.LAND_PINLINES_TEXTBOX)
    if "Vehicle" in self.layout.type_line:
        return psd.getLayer("Vehicle", LAYERS.PINLINES_TEXTBOX)
    return psd.getLayer(self.pinlines, LAYERS.PINLINES_TEXTBOX)

@cached_property
def pt_layer(self) -> Optional[ArtLayer]:
    if self.is_creature:
        if "Vehicle" in self.layout.type_line:
            return psd.getLayer("Vehicle", LAYERS.PT_BOX)
        return psd.getLayer(self.twins, LAYERS.PT_BOX)
    return psd.getLayerSet(LAYERS.PT_BOX)
```

### Key card data properties (from Scryfall)
Available on `self` inside any template class:

| Property | Type | Description |
|---|---|---|
| `self.layout` | object | Full Scryfall card data |
| `self.layout.type_line` | str | e.g. `"Creature — Human Wizard"` |
| `self.layout.artist` | str | Artist name |
| `self.pinlines` | str | Colour code for pinlines layer lookup, e.g. `"WU"`, `"B"`, `"Artifact"` |
| `self.background` | str | Colour code for background layer lookup |
| `self.twins` | str | Colour code for name/type box layer lookup |
| `self.is_land` | bool | True if card is a land |
| `self.is_creature` | bool | True if card is a creature |
| `self.is_legendary` | bool | True if card has Legendary supertype |
| `self.is_colorless` | bool | True if card is colourless |
| `self.is_transform` | bool | True if card is a transform card |
| `self.is_front` | bool | True if rendering the front face |
| `self.event` | threading.Event | Thread event for manual editing pause |

### Overriding static properties
Set these as class-level attributes to hard-disable optional layer types:
```python
class KaldheimTemplate(NormalTemplate):
    is_legendary = False      # never show legendary crown
    background_layer = None   # no background layer in PSD
    twins_layer = None        # no twins layer in PSD
```

---

## `src.helpers` — Photoshop Helper Functions

These abstract the raw Photoshop COM API into readable calls:

```python
import src.helpers as psd

# Layer lookups
psd.getLayer("Layer Name", parent_group)   # find ArtLayer by name inside a group
psd.getLayer("Layer Name")                 # find ArtLayer at document root
psd.getLayerSet("Group Name", parent)      # find LayerSet (group) by name

# Visibility / masks
psd.enable_mask(layer)               # enable a pixel mask on a layer
psd.enable_vector_mask(layer)        # enable a vector mask on a layer

# Text
psd.replace_text(layer, "old", "new")  # replace text content in a text layer

# Colours
psd.rgb_white()   # returns a Photoshop SolidColor for white
psd.rgb_black()   # returns a Photoshop SolidColor for black
```

---

## Art Actions — Post-Processing Scripts

### Purpose
After placing the card art in the art layer, templates can run an **art action** to transform it (e.g., make it look like a pencil sketch).

### How to define one
```python
from typing import Optional, Callable
from .actions import sketch, pencilsketch

class SketchTemplate(NormalTemplate):

    @property
    def art_action(self) -> Optional[Callable]:
        if ENV.TEST_MODE:
            return None   # skip in test mode
        return pencilsketch.run   # or sketch.run

    @property
    def art_action_args(self) -> Optional[dict]:
        return {
            'thr': self.event,              # threading event for pause support
            'rough_sketch': True,
            'black_and_white': False,
        }
```

Proxyshop calls `art_action(**art_action_args)` automatically after placing the art.

### Writing an action script

Action scripts use Photoshop's **ActionDescriptor / ActionReference** COM API — the same mechanism as recording a Photoshop Action and then scripting the steps:

```python
import photoshop.api as ps
from src import APP

sID = APP.stringIDToTypeID   # string → type ID (newer API)
cID = APP.charIDToTypeID     # 4-char code → type ID (legacy API)

def run():
    # Step: Gaussian Blur
    desc = ps.ActionDescriptor()
    desc.putUnitDouble(cID('Rds '), cID('#Pxl'), 65)   # Radius = 65px
    APP.executeAction(sID('gaussianBlur'), desc, ps.DialogModes.DisplayNoDialogs)

    # Step: Change blend mode to Color Dodge
    desc = ps.ActionDescriptor()
    ref = ps.ActionReference()
    ref.putEnumerated(cID('Lyr '), cID('Ordn'), cID('Trgt'))
    desc.putReference(cID('null'), ref)
    inner = ps.ActionDescriptor()
    inner.putEnumerated(cID('Md  '), cID('BlnM'), cID('CDdg'))  # Color Dodge
    desc.putObject(cID('T   '), cID('Lyr '), inner)
    APP.executeAction(cID('setd'), desc, ps.DialogModes.DisplayNoDialogs)
```

#### Common action patterns

| Operation | Method call |
|---|---|
| Apply Gaussian Blur | `sID('gaussianBlur')` + `putUnitDouble(cID('Rds '), cID('#Pxl'), radius)` |
| Invert layer | `APP.executeAction(cID('Invr'), ps.ActionDescriptor(), ...)` |
| Set blend mode | `inner.putEnumerated(cID('Md  '), cID('BlnM'), cID('CDdg'))` for Color Dodge |
| Set opacity | `inner.putUnitDouble(cID('Opct'), cID('#Prc'), 60)` |
| Merge visible | `APP.executeAction(sID('mergeVisible'), ...)` |
| Duplicate layer | `APP.executeAction(cID('Dplc'), ...)` |
| Filter Gallery | `APP.executeAction(1195730531, ...)` with `putEnumerated(cID('GEfk'), ...)` |
| Copy Merged | `APP.executeAction(sID('copyMerged'), ...)` |
| Paste | `APP.executeAction(cID('past'), ...)` |

#### Blend mode codes (`cID` 4-char)
| Mode | Code |
|---|---|
| Normal | `'Nrml'` |
| Multiply | `'Mltp'` |
| Color Dodge | `'CDdg'` |
| Hard Light | `'HrdL'` |

---

## Per-Template Configuration (`.toml`)

Plugin templates can expose user-configurable settings via `.toml` files stored in `plugins/<Name>/config/<TemplateClassName>.toml`.

Read settings in the template:
```python
from src import CFG

action = CFG.get_setting(
    section="ACTION",
    key="Sketch.Action",
    default="Advanced Sketch",
    is_bool=False
)

is_bw = CFG.get_setting(
    section="ACTION",
    key="Black.And.White",
    default=False    # is_bool defaults to True when default is bool
)
```

---

## Art File Naming Reference

Art images placed in `art/` are matched to Scryfall by filename. Optional tags control rendering:

| Tag | Format | Position | Example |
|---|---|---|---|
| Card name | plain | required | `Damnation.jpg` |
| Set code | `[SET]` | after name | `Damnation [TSR].jpg` |
| Collector # | `{num}` | after set | `Brainstorm [SLD] {175}.jpg` |
| Artist override | `(Name)` | anywhere before `$` | `Damnation (My Artist).jpg` |
| Creator name | `$Name` | **must be last** | `Damnation$MyName.jpg` |

**Supported art formats:** `jpg`, `jpeg`, `jpf`, `png`, `tif`, `webp` (webp requires PS 2022+)

---

## Required Fonts

Must be installed system-wide before running Proxyshop:

| Font | Used For |
|---|---|
| Beleren Proxy Bold | Card name, typeline, P/T |
| Proxyglyph | Mana symbols (fork of NDPMTG) |
| Plantin MT Pro (all variants) | Rules text |
| Beleren Smallcaps | Artist credit, misc |
| Gotham Medium | Collector info text |
| Magic The Gathering *(optional)* | Classic template |
| Matrix Bold *(optional)* | Colorshifted template |
| Mana *(optional)* | Additional card symbols |

---

## Creating a New Plugin

1. Create `plugins/<YourName>/` with this structure:
   ```
   plugins/YourName/
   ├── manifest.yml
   ├── templates/          ← your .psd file(s)
   ├── config/             ← optional .toml settings
   └── py/
       ├── __init__.py
       └── templates.py
   ```

2. Define `manifest.yml`:
   ```yaml
   PLUGIN:
     name: 'YourName'
     author: 'YourName'
     desc: 'Description of your plugin.'
     license: 'MPL-2.0'
     requires: '^1.13.0'
     version: '1.0.0'

   your-template.psd:
     name: 'Your Template'
     id: '<google_drive_id>'
     desc: 'Description.'
     templates: { Your Template: YourTemplateClass }
   ```

3. Define your template class in `py/templates.py`:
   ```python
   from src.templates import NormalTemplate, ExtendedMod
   import src.helpers as psd
   from src.enums.layers import LAYERS

   class YourTemplateClass(ExtendedMod, NormalTemplate):
       template_suffix = "Your Suffix"

       # Override only what differs from the base class
   ```

4. Place your `.psd` in `plugins/YourName/templates/`.

5. Launch Proxyshop — your plugin's templates will appear in the template selector.

---

---

## MTG-Autoproxy — JSX (ExtendScript) Approach

Source: `G:\My Drive\Magic the Gathering\Proxies\Automation\MTG-Autoproxy`

This is the **original** Photoshop automation approach (by Chilli-Axe, which Proxyshop was based on).  
Instead of Python + `photoshop-python-api`, it uses **Adobe ExtendScript (`.jsx`)** — JavaScript that runs _inside_ Photoshop directly, with no external process needed.

### Comparison: JSX vs Python (Proxyshop)

| Aspect | MTG-Autoproxy (JSX) | Proxyshop (Python) |
|---|---|---|
| Language | Adobe ExtendScript (ES3 JS) | Python 3.9–3.11 |
| API access | Direct (`app`, `executeAction`) | Via `photoshop-python-api` wrapper |
| Layer lookup | `doc.layers.getByName("name")` | `psd.getLayer("name", group)` |
| Run method | Drag `.jsx` onto Photoshop | `Proxyshop.exe` GUI |
| Card data | Python subprocess → JSON file | Direct Scryfall API calls |
| Configuration | `settings.jsx` global variables | `.toml` + `CFG.get_setting()` |
| Plugin system | `includeFolder()` loads `.jsx` files | `manifest.yml` + Python classes |

---

### Complete Layer Name Reference (`constants.jsx` → `LayerNames`)

These exact names must exist in any compatible `.psd` file:

```javascript
// Colour layers (used as sibling-layer names inside groups)
WHITE: "W",  BLUE: "U",  BLACK: "B",  RED: "R",  GREEN: "G",
WU: "WU",  UB: "UB",  BR: "BR",  RG: "RG",  GW: "GW",
ARTIFACT: "Artifact",  LAND: "Land",  GOLD: "Gold",  COLOURLESS: "Colourless"

// Frame layer groups
PT_BOX:                "PT Box"
TWINS:                 "Name & Title Boxes"
LEGENDARY_CROWN:       "Legendary Crown"
PINLINES_TEXTBOX:      "Pinlines & Textbox"
LAND_PINLINES_TEXTBOX: "Land Pinlines & Textbox"
COMPANION:             "Companion"
BACKGROUND:            "Background"
NYX:                   "Nyx"
BORDER:                "Border"
SHADOWS:               "Shadows"

// Text and Icons group children
TEXT_AND_ICONS:        "Text and Icons"
NAME:                  "Card Name"
NAME_SHIFT:            "Card Name Shift"        // used when transform icon present
TYPE_LINE:             "Typeline"
TYPE_LINE_SHIFT:       "Typeline Shift"         // used when colour indicator present
MANA_COST:             "Mana Cost"
EXPANSION_SYMBOL:      "Expansion Symbol"
EXPANSION_REFERENCE:   "Expansion Reference"    // invisible reference layer for symbol sizing
COLOUR_INDICATOR:      "Colour Indicator"
POWER_TOUGHNESS:       "Power / Toughness"
RULES_TEXT_NONCREATURE: "Rules Text - Noncreature"
RULES_TEXT_CREATURE:   "Rules Text - Creature"
TEXTBOX_REFERENCE:     "Textbox Reference"       // invisible reference for text scaling

// Art frames (invisible reference layers)
ART_FRAME:             "Art Frame"
FULL_ART_FRAME:        "Full Art Frame"
PLANESWALKER_ART_FRAME: "Planeswalker Art Frame"

// Legal group
LEGAL:                 "Legal"
ARTIST:                "Artist"
COLLECTOR:             "Collector"

// Transform / MDFC
TF_FRONT:              "tf-front"
TF_BACK:               "tf-back"
MDFC_FRONT:            "mdfc-front"
MDFC_BACK:             "mdfc-back"
```

> These names are **identical** between MTG-Autoproxy (JSX) and Proxyshop (Python). Any `.psd` built for one should work with the other.

---

### JSX Template Class System

```javascript
// BaseTemplate — the root for all templates
var BaseTemplate = Class({
    constructor: function(layout, file, file_path) {
        this.layout = layout;     // card data from Scryfall JSON
        this.file = file;         // art file to paste in
        this.load_template(file_path);  // opens the .psd in Photoshop
        this.text_layers = [];    // array of text layer objects to execute
        // ... artist, collector, set info populated here
    },
    template_file_name: function() { throw new Error("Not specified!"); },
    template_suffix: function() { return ""; },
    load_template: function(file_path) { /* app.open(template_file) */ },
    enable_frame_layers: function() { throw new Error("Not specified!"); },
    load_artwork: function() { paste_file(this.art_layer, this.file); },
    execute: function() {
        this.load_artwork();               // 1. paste art into art layer
        frame_layer(this.art_layer, this.art_reference);  // 2. scale & centre
        this.enable_frame_layers();        // 3. show correct colour layers
        for (var i = 0; i < this.text_layers.length; i++) {
            this.text_layers[i].execute(); // 4. populate all text fields
        }
        return this.layout.name + (suffix ? " (" + suffix + ")" : "");
    }
});
```

**Hierarchy:**
```
BaseTemplate
  └── ChilliBaseTemplate     (adds is_creature, is_legendary, is_land, is_companion)
        └── NormalTemplate   (standard M15 frame)
              ├── NormalExtendedTemplate   (strips reminder text, extended suffix)
              ├── NormalFullartTemplate
              ├── WomensDayTemplate        (no background, mask pinlines for legendary)
              ├── StargazingTemplate       (forces is_nyx = true)
              ├── MasterpieceTemplate      (forces Bronze twins/background)
              ├── ExpeditionTemplate       (no mana cost, no creatures)
              ├── SnowTemplate
              ├── MiracleTemplate          (no creatures)
              ├── TransformBackTemplate
              │     └── TransformFrontTemplate
              ├── MDFCBackTemplate
              │     └── MDFCFrontTemplate
              ├── MutateTemplate
              ├── AdventureTemplate
              └── IxalanTemplate
```

**Creating a custom template:**
```javascript
var MyTemplate = Class({
    extends_: NormalTemplate,
    template_file_name: function() { return "my-template"; },  // → /templates/my-template.psd
    template_suffix: function() { return "My Suffix"; },
    constructor: function(layout, file, file_path) {
        // Optionally modify layout before calling super
        layout.oracle_text = strip_reminder_text(layout.oracle_text);
        this.super(layout, file, file_path);
        // Add extra text layers after super
    },
    enable_frame_layers: function() {
        this.super();  // call parent to enable standard layers
        // Then enable extra template-specific layers
        var docref = app.activeDocument;
        docref.layers.getByName("My Layer Group")
              .layers.getByName(this.layout.pinlines).visible = true;
    }
});
```

---

### JSX Helpers (`helpers.jsx`)

```javascript
// Colour objects
rgb_black()   // → SolidColor {0, 0, 0}
rgb_white()   // → SolidColor {255, 255, 255}
get_text_layer_colour(layer)  // safely reads layer.textItem.color, falls back to black

// Layer geometry
compute_layer_dimensions(layer)        // → { width, height } from bounds
compute_text_layer_dimensions(layer)   // rasterises copy to measure actual text size
select_layer_pixels(layer)             // selects bounding box of layer
clear_selection()

// Art framing
frame_layer(layer, reference_layer)
// Scales layer to FILL reference (Math.max ratio), then centres horizontally + vertically

frame_expansion_symbol(layer, reference_layer, centered)
// Scales symbol to FIT reference (Math.min ratio), aligns vertically (+ optionally horizontally)

// Layer alignment (requires an active selection)
align_vertical()     // centres active layer vertically within selection
align_horizontal()   // centres active layer horizontally within selection

// Masks
enable_active_layer_mask()   // enables the mask on the currently active layer
disable_active_layer_mask()

// Text
replace_text(layer, find_string, replace_string)  // Photoshop find/replace via ActionDescriptor

// Art file loading
paste_file(layer, file)         // load file, select-all, copy, close, paste into layer
paste_file_into_new_layer(file) // creates new layer then calls paste_file
insert_scryfall_scan(image_url, file_path)  // downloads scan via Python, pastes into new layer

// Layer creation
create_new_layer(layer_name)  // adds layer below active, sets blend mode Normal

// Image effects
apply_stroke(stroke_weight, stroke_colour)   // outer stroke via layer effects ActionDescriptor
VibrantSaturation(VibValue, SatValue)        // adjust vibrance/saturation via ActionDescriptor

// Saving
save_and_close(file_name, file_path)  // save as PNG to /out/, then close without saving

// Utilities
strip_reminder_text(oracle_text)   // removes (reminder text) in parentheses
in_array(array, item)              // polyfill for Array.indexOf
array_index(array, item)
includeFolder(fName)               // dynamically load all .jsx files in subfolders (plugin system)
```

### Critical difference: `frame_layer` uses `Math.max` (fill), `frame_expansion_symbol` uses `Math.min` (fit)

```javascript
// Art must FILL the frame (crop OK, no letterboxing)
var scale = 100 * Math.max(ref_w / layer_w, ref_h / layer_h);

// Symbol must FIT inside its reference box (no cropping)
var scale = 100 * Math.min(ref_w / layer_w, ref_h / layer_h);
```

---

### Text Layer Classes (`text_layers.jsx`)

Each item in `this.text_layers[]` is an object with an `execute()` method:

| Class | Purpose |
|---|---|
| `TextField` | Plain text — just set `.textItem.contents` |
| `ScaledTextField` | Text that shrinks horizontally if it would overlap a reference layer (e.g. card name vs mana cost) |
| `BasicFormattedTextField` | Text with mana symbol substitution (e.g. `{W}` → NDPMTG font character) |
| `FormattedTextArea` | Rules text with mana symbols, italic flavour text, paragraph formatting |
| `CreatureFormattedTextArea` | Like FormattedTextArea but also resizes the textbox to make room for the P/T box |
| `ExpansionSymbolField` | Sets expansion symbol character, applies rarity colour gradient, scales to reference |

---

### Mana Symbol Font Encoding (NDPMTG font)

Scryfall returns mana cost strings like `{2}{W}{U}`. These are translated to NDPMTG font characters:

```javascript
var symbols = {
    "{T}": "ot",    // tap
    "{W}": "ow",    // white
    "{U}": "ou",    // blue
    "{B}": "ob",    // black
    "{R}": "or",    // red
    "{G}": "og",    // green
    "{C}": "oc",    // colourless
    "{X}": "ox",    // X cost
    "{0}": "o0", "{1}": "o1", /* ... */ "{16}": "oG",
    "{W/U}": "QqLS",  // hybrid
    "{E}": "e",     // energy
    // ... etc.
};
```

The `BasicFormattedTextField` class iterates this dictionary to replace all occurrences in the text before setting the layer contents.

---

### Scryfall Data Integration (JSX → Python → JSON)

The JSX scripts cannot make HTTP requests directly. They call Python scripts via `app.system()`:

```javascript
// helpers.jsx
function retrieve_scryfall_scan(image_url, file_path) {
    var cmd = "python \"" + file_path + "/scripts/get_card_scan.py\" \"" + image_url + "\"";
    app.system(cmd);
    return new File(file_path + "/scripts/card.jpg");
}
```

Python scripts (`scripts/get_card_info.py`, `scripts/get_card_scan.py`) write their output to:
- `scripts/card.json` — full Scryfall card data
- `scripts/card.jpg` — downloaded card scan image

JSX then reads the JSON via `json2.js` and constructs the `layout` object from it.

---

## Debugging Tips

- **Error logs:** `logs/error.txt` — always check here first.
- **Test mode:** `ENV.TEST_MODE` — skip art actions and other slow operations in automated tests.
- **Photoshop busy error:** Close all dialogs, ensure no text tool is active, restart fresh.
- **Text too large / not scaling:** Photoshop → Edit → Preferences → Units & Rulers → set **Rulers: Pixels**, **Type: Points**.
- **Layer hide won't work:** Never use visibility toggle — set **Opacity to 0** instead.
- **RPC not responding:** Only one Photoshop install allowed; must use a real installer (no portable).
