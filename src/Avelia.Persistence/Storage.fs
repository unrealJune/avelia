namespace Avelia.Persistence

open System.IO

module Storage =
    let defaultDbPath () =
        let appData =
            System.Environment.GetFolderPath System.Environment.SpecialFolder.LocalApplicationData

        Path.Combine(appData, "Avelia", "avelia.db")
