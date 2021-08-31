
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open System
open System.IO
open System.Net
open System.Net.Http
open CasCap.Services
open CasCap.Models
open Google.Apis.Auth.OAuth2
open FSharp.Json
open System.Collections.Generic
open System.Collections

type Media = {
    MediaId : string
    Filename : string
    mutable FullPath : option<string>
    CreationTime : DateTime    
    BaseUrl: string
    mutable Description: string
    Url: string
    mutable MediaExpunged : bool
}

type AlbumEntry = { 
    AlbumId : string
    mutable Title : string 
    Url : string 
    mutable CoverPhotoId : string
    mutable Media : list<Media>  
    mutable AlbumExpunged : bool
}

type AlbumEntries = {
    mutable Albums : list<AlbumEntry> 
}


[<EntryPoint>]
let main args =    
    match args with
    | [|dirname|] -> 
        let fname = IO.Path.Join [|dirname;"GPhotosBackup.json"|]
        printfn "Downloading list of albums from server."
        let servAlbums = 
            let secret = GoogleClientSecrets.FromFile "secret.json"

            let options = GooglePhotosOptions()
            options.User <- "vincenzoml@gmail.com" // TODO: configure this
            options.ClientId <- secret.Secrets.ClientId
            options.ClientSecret <- secret.Secrets.ClientSecret
            options.Scopes <- [|GooglePhotosScope.ReadOnly|]
            let handler = new HttpClientHandler() 
            handler.AutomaticDecompression <- DecompressionMethods.GZip ||| DecompressionMethods.Deflate 
            
            let client = new HttpClient()
            client.BaseAddress <- Uri(options.BaseAddress)    
            
            let logger = (new LoggerFactory()).CreateLogger<GooglePhotosService>()
                
            let serv = GooglePhotosService(logger,Options.Create options,client)
            
            let login = serv.LoginAsync().Result   
            if login then
                let albumsList = 
                    serv.GetAlbumsAsync().Result |>                     
                    Seq.map 
                        (fun (album : Album) ->                                                     
                            printfn "Downloading album TOC for album %A." album.title
                            {
                                AlbumId = album.id
                                Title = album.title                        
                                CoverPhotoId = album.coverPhotoMediaItemId
                                Url = album.productUrl
                                AlbumExpunged = false
                                Media = 
                                    (serv.GetMediaItemsByAlbumAsync album.id).Result |> 
                                    Seq.map (fun (mediaItem : MediaItem) -> {
                                        MediaId = mediaItem.id
                                        Filename = mediaItem.filename
                                        FullPath = None
                                        BaseUrl = mediaItem.baseUrl
                                        Description = mediaItem.description
                                        Url = mediaItem.productUrl
                                        CreationTime = mediaItem.mediaMetadata.creationTime      
                                        MediaExpunged = false                                                         
                                    }) |>
                                    Seq.toList
                            }) |>
                    Seq.toList
                let albumEntries = { Albums = albumsList }
                albumEntries
            else
                failwith "Error in login."
        let albums =         
            if File.Exists fname then
                printfn "Merging server and client data from file %A." fname
                let clientAlbums = Json.deserialize<AlbumEntries> (File.ReadAllText fname)
                let saMap = Map(seq { for sa in servAlbums.Albums do (sa.AlbumId,sa) })
                let caMap = Map(seq { for ca in servAlbums.Albums do (ca.AlbumId,ca) }) 
                for ca in clientAlbums.Albums do
                    try 
                        let sa = saMap.[ca.AlbumId]
                        let saMediaMap = Map(seq { for smi in sa.Media do (smi.MediaId,smi) })
                        let caMediaMap = Map(seq { for cmi in ca.Media do (cmi.MediaId,cmi) })
                        for cam in ca.Media do
                            try
                                let sam = saMediaMap.[cam.MediaId]
                                cam.Description <- sam.Description
                            with _ -> 
                                cam.MediaExpunged <- true
                        for sam in sa.Media do
                            if not <| caMediaMap.ContainsKey sam.MediaId 
                            then ca.Media <- sam::ca.Media
                    with _ -> 
                        ca.AlbumExpunged <- true                
                for sa in servAlbums.Albums do
                    try
                        let ca = caMap.[sa.AlbumId]
                        ca.Title <- sa.Title
                        ca.CoverPhotoId <- sa.CoverPhotoId
                    with _ ->
                        clientAlbums.Albums <- sa::clientAlbums.Albums                                
                clientAlbums
            else
                servAlbums

        printfn "Reading contents of directory %A." dirname
        let kv = Seq.map (fun (fpath : string) -> (Path.GetFileName fpath,Path.GetRelativePath(dirname,fpath))) (Directory.EnumerateFileSystemEntries(dirname,"*.jpg",SearchOption.AllDirectories))
        let files = Map.ofSeq kv
        printfn "Assigning missing file names."
        for album in albums.Albums do
            for entry in album.Media do
                // if Option.isNone entry.FullPath then // Uncomment this line if this phase becomes too slow?
                    try                     
                        entry.FullPath <- Some files.[entry.Filename]
                    with _ -> () // TODO: download from google

        File.WriteAllText (fname,Json.serialize albums)
    | _ ->
        printfn "This command takes exactly one argument, the directory where the collection resides."
        // printfn "https://nanogallery2.nanostudio.org/"
        // "See https://github.com/f2calv/CasCap.Apis.GooglePhotos#google-photos-api-set-up and https://stackoverflow.com/questions/65184355/error-403-access-denied-from-google-authentication-web-api-despite-google-acc"    
    0