# Flujo

Flujo genera y edita documentos de Writer y Calc en local. Un modelo en Ollama propone un JSON de operaciones; LibreOffice las aplica sobre una plantilla; una comprobación de calidad acepta o rechaza el resultado. Las plantillas y los documentos de origen no se modifican: cada ejecución escribe una carpeta nueva.

## Requisitos

- .NET 10
- LibreOffice para Windows, con `python.exe` en la misma carpeta que `soffice.exe` (hace falta el módulo UNO)
- Ollama en ejecución, con el modelo indicado en `appsettings.json`

LibreOffice se busca en `Program Files` y en `%LOCALAPPDATA%\LibreOffice`. Si está en otra ruta, define `LIBREOFFICE_PATH` con la ruta de `soffice.exe`.

## Comprobar el entorno

Desde la raíz del repositorio:

```powershell
dotnet run --project src/Flujo.Cli -- verificar-entorno
```

El comando escribe las rutas de LibreOffice, de su Python y del puente UNO, y comprueba que Ollama responde con el modelo configurado. Sale con código 1 si falta alguno.

## Generar un documento

```powershell
dotnet run --project src/Flujo.Cli -- generar --modo crear --plantilla templates/writer-informe --tarea examples/tarea-informe.md
```

```powershell
dotnet run --project src/Flujo.Cli -- generar --modo crear --plantilla templates/calc-libro --tarea examples/tarea-libro.md
```

Hay que ejecutarlo desde la raíz: `appsettings.json`, las instrucciones y el puente se resuelven respecto al directorio actual.

| Argumento | Uso |
| --- | --- |
| `--modo` | `crear`, `editar` o `regenerar` (también `create`, `edit`, `regenerate`) |
| `--plantilla` | Carpeta con `manifest.yaml` y, si aplica, el archivo de origen |
| `--tarea` | Markdown con el material que el modelo puede usar |
| `--documento` | Documento existente. Obligatorio al editar o regenerar |
| `--secciones` | Ids de marcador separados por coma. Obligatorio al regenerar |
| `--proyecto` | Instrucciones extra de ese trabajo, en Markdown |
| `--salida` | Carpeta de salida. Por defecto, `output` |

Editar parte del documento ya generado y aplicar las operaciones nuevas sobre una copia:

```powershell
dotnet run --project src/Flujo.Cli -- generar --modo editar --plantilla templates/writer-informe --tarea examples/tarea-informe.md --documento output/informe/<version>/informe.odt
```

Regenerar solo los marcadores indicados, volviendo a partir de la plantilla:

```powershell
dotnet run --project src/Flujo.Cli -- generar --modo regenerar --plantilla templates/writer-informe --tarea examples/tarea-informe.md --documento templates/writer-informe/plantilla.odt --secciones desarrollo
```

Si la comprobación no pasa, el proceso termina con error y deja la versión en disco para poder revisarla.

## Qué queda en la salida

Cada ejecución crea `output/<nombre>/<yyyyMMdd-HHmmss>/` con:

- el documento nativo (`.odt` o `.ods`)
- el PDF, si el manifiesto tiene `exportPdf: true`
- `inspection.json`, con el texto, los títulos, las tablas, los marcadores y las fórmulas leídos del archivo
- `quality-report.json`, con el resultado de las reglas

## Plantillas

Una plantilla es una carpeta con `manifest.yaml` (o `manifest.json`) y, de forma opcional, `instructions.md` y un archivo de origen.

Writer acepta `.odt`, `.ott` y `.docx`. Calc acepta `.ods`, `.ots` y `.xlsx`. Si el origen no está en el formato nativo, LibreOffice lo convierte. Si `sourceFile` se omite, el documento se crea en blanco.

```yaml
name: informe
kind: writer          # writer o calc
sourceFile: plantilla.odt
exportPdf: true
markers:
  - id: titulo
    kind: text
    target: "{{titulo}}"
requiredSections:
  - "1. Título"
checkableRules:
  - required_sections
  - required_markers
```

En Calc, un marcador de celda usa `kind: cell` y un destino como `Movimientos!B2`. `allowedFormulas` lista las celdas donde el modelo puede escribir una fórmula. `preservedFormulas` fija las que deben seguir igual.

El modelo solo puede devolver estas operaciones:

- Writer: `replace_marker`, `insert_heading`, `insert_paragraph`, `insert_list`, `insert_table`, `update_table`
- Calc: `set_cell`, `set_formula`, `write_range`, `add_sheet`, `rename_sheet`

No se ejecuta código, macros ni comandos que vengan en la respuesta.

## Comprobación

Las reglas se activan por nombre en `checkableRules`:

| Regla | Qué exige |
| --- | --- |
| `required_sections` | Cada texto de `requiredSections` aparece en un título, una hoja o el texto |
| `required_markers` | No quedan marcadores `{{id}}` sin rellenar. Al regenerar, solo cuenta los pedidos |
| `no_invented_references` | Un año entre paréntesis tiene que estar en la tarea o en `allowedReferences` |
| `heading_numbering` | Los títulos usan numeración decimal (`1.`, `1.1.`) |
| `preserved_formulas` | Las fórmulas declaradas siguen en su celda |

Si `exportPdf` es verdadero, también se exige un PDF con contenido.

## Configuración

`appsettings.json` en el directorio de trabajo:

```json
{
  "Ollama": {
    "BaseUrl": "http://127.0.0.1:11434",
    "Model": "qwen3.5:latest",
    "TimeoutSeconds": 180
  },
  "LibreOffice": {
    "BridgeScript": "bridge/worker.py",
    "ScriptsDirectory": "bridge/vendor/libreoffice-skills/scripts",
    "TimeoutSeconds": 180
  },
  "Storage": {
    "AllowedRoots": [ "." ],
    "OutputRoot": "output"
  },
  "Instructions": {
    "GlobalPath": "instructions/global.md",
    "WriterPath": "instructions/writer.md",
    "CalcPath": "instructions/calc.md"
  }
}
```

`AllowedRoots` limita las carpetas que la herramienta puede leer y escribir. Las variables `OLLAMA_HOST`, `OLLAMA_MODEL` y `LIBREOFFICE_PATH` sustituyen los valores correspondientes.

Las instrucciones se apilan en este orden: globales, del tipo de documento, del proyecto, de la plantilla y de la tarea.

## Pruebas

```powershell
dotnet test Flujo.slnx
```

Las pruebas de Ollama se omiten si el modelo local no está disponible. Las de documentos necesitan LibreOffice y tardan varios minutos, porque cada operación abre una instancia propia.
