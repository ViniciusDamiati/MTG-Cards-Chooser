# MTG Card Chooser

A C# console application that searches for Magic: The Gathering card files based on a list and copies them to an output folder.

## Features

- Reads a list of card names from a text file
- **Searches recursively**: Automatically searches through all subdirectories in the source folder, no matter how deeply nested
- Copies found cards to an output folder
- Generates a report of missing cards
- Case-insensitive card name matching
- Handles duplicate file names automatically
- **Smart filename matching**: Extracts card names from files with metadata like `Card Name (Type) [Set] {Number}`

## Configuration

Edit the `config.txt` file to configure the application. This is a simple text file with key=value pairs:

```
InputCardsFile=D:\MyLists\cards.txt
SourceFolder=C:\MTG\Cards
OutputFolder=C:\MTG\Output
MissingCardsReport=missing_cards.txt
```

### Configuration Options:

- **InputCardsFile**: Full or relative path to the text file containing card names (one per line)
- **SourceFolder**: Directory where your card files are stored (searches recursively)
- **OutputFolder**: Directory where found cards will be copied
- **MissingCardsReport**: Filename for the missing cards report (will be saved in the OutputFolder)

**Notes:** 
- You can add comments in the config file by starting a line with #
- InputCardsFile can be a full path (e.g., D:\MyLists\cards.txt) or relative path (e.g., cards.txt)
- The missing cards report will be saved in the OutputFolder

## Usage

1. **Configure the application**:
   - Open `config.txt`
   - Set `SourceFolder` to the directory containing your card files
   - Set `OutputFolder` to where you want the found cards copied
   - (Optional) Adjust other settings as needed

2. **Create your card list**:
   - Edit `cards.txt` (or the file specified in InputCardsFile)
   - Add one card per line
   - The application supports two formats:
     
     **Simple format** (card name only):
     ```
     Black Lotus
     Lightning Bolt
     Counterspell
     ```
     
     **Extended format** (quantity, card name, set code, collector number):
     ```
     1 Allosaurus Shepherd (2X2) 132
     1 Arbor Adherent (TDC) 42
     1 Assassin's Trophy (MKM) 187
     ```
   - In the extended format, only the card name is used for matching (quantity, set code, and collector number are ignored)

3. **Run the application**:
   ```bash
   dotnet run
   ```

   Or build and run the executable:
   ```bash
   dotnet build
   cd bin\Debug\net9.0
   CardChooser.exe
   ```

## Output

The application will:
- Display progress as it searches for each card
- Copy all found card files to the output folder
- Create a `missing_cards.txt` file listing any cards that weren't found
- Display a summary with statistics

## Example Output

```
=== MTG Card Chooser ===
Input Cards File: cards.txt
Source Folder: C:\MTG\Cards
Output Folder: C:\MTG\Output

Reading card list...
Found 5 card(s) in the input file.

Scanning source folder...
Found 1523 file(s) in source folder.

Processing cards...
Searching for 'Black Lotus'... FOUND (1 file(s))
  Copied: Black Lotus.jpg -> Black Lotus.jpg
Searching for 'Lightning Bolt'... FOUND (2 file(s))
  Copied: Lightning Bolt.jpg -> Lightning Bolt.jpg
  Copied: Lightning Bolt.png -> Lightning Bolt.png
Searching for 'Counterspell'... MISSING

=== Summary ===
Total cards in list: 5
Cards found: 4
Cards missing: 1
Files copied: 6

Missing cards:
  - Counterspell

Process completed successfully!
```

## Requirements

- .NET 9.0 SDK or later
- Windows, macOS, or Linux

## Notes

- **Recursive Search**: The application automatically searches through ALL subdirectories within the source folder
  - Example: If SourceFolder is `C:\MTG\Cards`, it will find cards in:
    - `C:\MTG\Cards\Black Lotus.jpg`
    - `C:\MTG\Cards\Vintage\Sol Ring.png`
    - `C:\MTG\Cards\Modern\Aggro\Lightning Bolt.jpg`
    - `C:\MTG\Cards\Commander\Blue\Counterspell.jpg`
  - No matter how many nested folders deep, the application will find the cards
- Card matching is case-insensitive
- Multiple files with the same card name (different extensions) will all be copied
- **File Overwriting**: If a file with the same name already exists in the output folder, it will be overwritten with the new version from the source folder
- **Smart filename matching**: The application can match cards even when filenames include metadata:
  - Example: A file named `Rhystic Study (Normal) [JMP] {169}.jpg` will match a card named "Rhystic Study"
  - The application extracts the card name by removing content in parentheses `()`, brackets `[]`, and braces `{}`
  - This works with various filename formats: `Card Name (Type) [Set] {Number}`, `Card Name [Set]`, `Card Name (Type)`, etc.
