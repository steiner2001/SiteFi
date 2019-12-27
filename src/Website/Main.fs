namespace Website

open System
open WebSharper
open WebSharper.Sitelets
open WebSharper.UI
open WebSharper.UI.Server

type EndPoint =
    | [<EndPoint "GET /">] Home
    | [<EndPoint "GET /blog">] Article of slug:string

module Markdown =
    open Markdig

    let pipeline =
        MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseGridTables()
            .UseListExtras()
            .UseEmphasisExtras()
            .UseGenericAttributes()
            .UseAutoLinks()
            .UseTaskLists()
            .UseMediaLinks()
            .UseCustomContainers()
            .UseMathematics()
            .UseEmojiAndSmiley()
            .UseAdvancedExtensions()
            .UseYamlFrontMatter()
            .Build()

    let Convert content = Markdown.ToHtml(content, pipeline)

module Yaml =
    open System.Text.RegularExpressions
    open YamlDotNet.Serialization

    let SplitIntoHeaderAndContent (source: string) =
        let delimRE = Regex("^---\\w*\r?$", RegexOptions.Compiled ||| RegexOptions.Multiline)
        let searchFrom = if source.StartsWith("---") then 3 else 0
        let m = delimRE.Match(source, searchFrom)
        if m.Success then
            source.[searchFrom..m.Index-1], source.[m.Index + m.Length..]
        else
            "", source

    let OfYaml<'T> (yaml: string) =
        let deserializer = (new DeserializerBuilder()).Build()
        if String.IsNullOrWhiteSpace yaml then
            deserializer.Deserialize<'T>("{}")
        else
            let yaml = deserializer.Deserialize<'T>(yaml)
            eprintfn "DEBUG/YAML=%A" yaml
            yaml

module Helpers =
    open System.IO
    open System.Text.RegularExpressions

    let NULL_TO_EMPTY (s: string) = match s with null -> "" | t -> t

    // Return (fullpath, filename-without-extension, (year, month, day), slug, extension)
    let (|ArticleFile|_|) (fullpath: string) =
        let filename = Path.GetFileName(fullpath)
        let filenameWithoutExt = Path.GetFileNameWithoutExtension(fullpath)
        let r = new Regex("([0-9]+)-([0-9]+)-([0-9]+)-(.+)\.(md)")
        if r.IsMatch(filename) then
            let a = r.Match(filename)
            let V (i: int) = a.Groups.[i].Value
            let I = Int32.Parse
            Some (fullpath, filenameWithoutExt, (I (V 1), I (V 2), I (V 3)), V 4, V 5)
        else
            None

module Site =
    open System.IO
    open WebSharper.UI.Html

    type MainTemplate = Templating.Template<"index.html", serverLoad=Templating.ServerLoad.WhenChanged>

    type [<CLIMutable>] Article =
        {
            title: string
            subtitle: string
            url: string
            content: string
            date: string
        }

    let Articles () : Map<string, Article> =
        let folder = Path.Combine (__SOURCE_DIRECTORY__, "posts")
        if Directory.Exists folder then
            Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories)
            |> Seq.toList
            |> List.choose (Helpers.(|ArticleFile|_|))
            |> List.fold (fun map (fullpath, fname, (year, month, day), slug, extension) ->
                eprintfn "Found file: %s" fname
                let header, content =
                    File.ReadAllText fullpath
                    |> Yaml.SplitIntoHeaderAndContent
                let article = Yaml.OfYaml<Article> header
                let title = Helpers.NULL_TO_EMPTY article.title
                let url = "/blog/" + fname + ".html" // Note: we are hardcoding the URL scheme here
                let subtitle = Helpers.NULL_TO_EMPTY article.subtitle
                let content = Markdown.Convert content
                let date = String.Format("{0:D4}{1:D2}{2:D2}", year, month, day)
                Map.add fname
                    {
                        title = title
                        subtitle = subtitle
                        url = url
                        content = content
                        date = date
                    } map
            ) Map.empty
        else
            eprintfn "warning: the posts folder (%s) does not exist." folder
            Map.empty

    let Menu articles =
        let latest =
            articles
            |> Map.toSeq
            |> Seq.truncate 5
            |> Map.ofSeq
        [
            "Home", "/", Map.empty
            "Latest", "#", latest
        ]

    let private head =
        __SOURCE_DIRECTORY__ + "/js/Client.head.html"
        |> File.ReadAllText
        |> Doc.Verbatim

    let Page (title: option<string>) hasBanner articles (body: Doc) =
        MainTemplate()
#if !DEBUG
            .ReleaseMin(".min")
#endif
            .NavbarOverlay(if hasBanner then "overlay-bar" else "")
            .Head(head)
            .Title(
                match title with
                | None -> ""
                | Some t -> t + " | "
            )
            .TopMenu(Menu articles |> List.map (function
                | text, url, map when Map.isEmpty map ->
                    MainTemplate.TopMenuItem()
                        .Text(text)
                        .Url(url)
                        .Doc()
                | text, _, children ->
                    let items =
                        children
                        |> Map.toList
                        |> List.sortByDescending (fun (key, item) -> item.date)
                        |> List.map (fun (key, item) ->
                            MainTemplate.TopMenuDropdownItem()
                                .Text(item.title)
                                .Url(item.url)
                                .Doc())
                    MainTemplate.TopMenuItemWithDropdown()
                        .Text(text)
                        .DropdownItems(items)
                        .Doc()
            ))
            .DrawerMenu(Menu articles |> List.map (fun (text, url, children) ->
                MainTemplate.DrawerMenuItem()
                    .Text(text)
                    .Url(url)
                    .Children(
                        match url with
                        | "/blog" ->
                            ul []
                                (children
                                |> Map.toList
                                |> List.sortByDescending (fun (_, item) -> item.date)
                                |> List.map (fun (_, item) ->
                                    MainTemplate.DrawerMenuItem()
                                        .Text(item.title)
                                        .Url(item.url)
                                        .Doc()
                                ))
                        | _ -> Doc.Empty
                    )
                    .Doc()
            ))
            .Body(body)
            .Doc()
        |> Content.Page

    let BlogSidebar (articles: Map<string, Article>) =
        articles
        |> Map.toList
        |> List.sortByDescending (fun (_, article) -> article.date)
        |> List.map (fun (_, item) ->
            let tpl =
                MainTemplate.SidebarItem()
                    .Title(item.title)
                    .Url(item.url)
            tpl.Doc()
        )
        |> Doc.Concat
    
    let PLAIN html =
        div [Attr.Create "ws-preserve" ""] [Doc.Verbatim html]

    let ArticlePage articles (article: Article) =
        MainTemplate.Article()
            .Title(article.title)
            .Subtitle(Doc.Verbatim article.subtitle)
            .Sidebar(BlogSidebar articles)
            .Content(PLAIN article.content)
            .Doc()
        |> Page (Some article.title) false articles

    let Main articles =
        Application.MultiPage (fun (ctx: Context<_>) -> function
            | Home ->
                MainTemplate.HomeBody()
                    .ArticleList(
                        Doc.Concat [
                            for (_, article) in Map.toList articles ->
                                MainTemplate.ArticleCard()
                                    .Author("My name")
                                    .Title(article.title)
                                    .Url(article.url)
                                    .Date(article.date)
                                    .Doc()
                        ]                        
                    )
                    .Doc()
                |> Page None false articles
            | Article p ->
                ArticlePage articles articles.[p]
        )

[<Sealed>]
type Website() =
    let articles = Site.Articles ()

    interface IWebsite<EndPoint> with
        member this.Sitelet = Site.Main articles
        member this.Actions = [
            Home
            for (slug, _) in Map.toList articles do
                Article slug
        ]

[<assembly: Website(typeof<Website>)>]
do ()
