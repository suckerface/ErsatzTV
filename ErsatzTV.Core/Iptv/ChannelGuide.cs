﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using ErsatzTV.Core.Domain;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using static LanguageExt.Prelude;

namespace ErsatzTV.Core.Iptv
{
    public class ChannelGuide
    {
        private readonly List<Channel> _channels;
        private readonly string _host;
        private readonly string _scheme;

        public ChannelGuide(string scheme, string host, List<Channel> channels)
        {
            _scheme = scheme;
            _host = host;
            _channels = channels;
        }

        public string ToXml()
        {
            using var ms = new MemoryStream();
            using var xml = XmlWriter.Create(ms);
            xml.WriteStartDocument();

            xml.WriteStartElement("tv");
            xml.WriteAttributeString("generator-info-name", "ersatztv");

            foreach (Channel channel in _channels.OrderBy(c => c.Number))
            {
                xml.WriteStartElement("channel");
                xml.WriteAttributeString("id", channel.Number);

                xml.WriteStartElement("display-name");
                xml.WriteAttributeString("lang", "en");
                xml.WriteString(channel.Name);
                xml.WriteEndElement(); // display-name

                xml.WriteStartElement("icon");
                string logo = Optional(channel.Artwork).Flatten()
                    .Filter(a => a.ArtworkKind == ArtworkKind.Logo)
                    .HeadOrNone()
                    .Match(
                        artwork => $"{_scheme}://{_host}/iptv/logos/{artwork.Path}",
                        () => $"{_scheme}://{_host}/images/ersatztv-500.png");
                xml.WriteAttributeString("src", logo);
                xml.WriteEndElement(); // icon

                xml.WriteEndElement(); // channel
            }

            foreach (Channel channel in _channels.OrderBy(c => c.Number))
            {
                foreach (PlayoutItem playoutItem in channel.Playouts.Collect(p => p.Items).OrderBy(i => i.Start))
                {
                    string start = playoutItem.StartOffset.ToString("yyyyMMddHHmmss zzz").Replace(":", string.Empty);
                    string stop = playoutItem.FinishOffset.ToString("yyyyMMddHHmmss zzz").Replace(":", string.Empty);

                    string title = playoutItem.MediaItem switch
                    {
                        Movie m => m.MovieMetadata.HeadOrNone().Map(mm => mm.Title ?? string.Empty)
                            .IfNone("[unknown movie]"),
                        Episode e => e.Season.Show.ShowMetadata.HeadOrNone().Map(em => em.Title ?? string.Empty)
                            .IfNone("[unknown show]"),
                        _ => "[unknown]"
                    };

                    string subtitle = playoutItem.MediaItem switch
                    {
                        Episode e => e.EpisodeMetadata.HeadOrNone().Match(
                            em => em.Title ?? string.Empty,
                            () => string.Empty),
                        _ => string.Empty
                    };

                    string description = playoutItem.MediaItem switch
                    {
                        Movie m => m.MovieMetadata.HeadOrNone().Map(mm => mm.Plot ?? string.Empty).IfNone(string.Empty),
                        Episode e => e.EpisodeMetadata.HeadOrNone().Map(em => em.Plot ?? string.Empty)
                            .IfNone(string.Empty),
                        _ => string.Empty
                    };

                    string contentRating = playoutItem.MediaItem switch
                    {
                        // TODO: re-implement content rating
                        // Movie m => m.MovieMetadata.HeadOrNone().Map(mm => mm.ContentRating).IfNone(string.Empty),
                        _ => string.Empty
                    };

                    xml.WriteStartElement("programme");
                    xml.WriteAttributeString("start", start);
                    xml.WriteAttributeString("stop", stop);
                    xml.WriteAttributeString("channel", channel.Number);

                    if (playoutItem.MediaItem is Movie movie)
                    {
                        xml.WriteStartElement("category");
                        xml.WriteAttributeString("lang", "en");
                        xml.WriteString("Movie");
                        xml.WriteEndElement(); // category

                        Option<MovieMetadata> maybeMetadata = movie.MovieMetadata.HeadOrNone();
                        if (maybeMetadata.IsSome)
                        {
                            MovieMetadata metadata = maybeMetadata.ValueUnsafe();

                            if (metadata.Year.HasValue)
                            {
                                xml.WriteStartElement("date");
                                xml.WriteString(metadata.Year.Value.ToString());
                                xml.WriteEndElement(); // date
                            }

                            string poster = Optional(metadata.Artwork).Flatten()
                                .Filter(a => a.ArtworkKind == ArtworkKind.Poster)
                                .HeadOrNone()
                                .Match(
                                    artwork => $"{_scheme}://{_host}/artwork/posters/{artwork.Path}",
                                    () => string.Empty);

                            if (!string.IsNullOrWhiteSpace(poster))
                            {
                                xml.WriteStartElement("icon");
                                xml.WriteAttributeString("src", poster);
                                xml.WriteEndElement(); // icon
                            }
                        }
                    }

                    xml.WriteStartElement("title");
                    xml.WriteAttributeString("lang", "en");
                    xml.WriteString(title);
                    xml.WriteEndElement(); // title

                    if (!string.IsNullOrWhiteSpace(subtitle))
                    {
                        xml.WriteStartElement("sub-title");
                        xml.WriteAttributeString("lang", "en");
                        xml.WriteString(subtitle);
                        xml.WriteEndElement(); // subtitle
                    }

                    xml.WriteStartElement("previously-shown");
                    xml.WriteEndElement(); // previously-shown

                    if (playoutItem.MediaItem is Episode episode)
                    {
                        Option<ShowMetadata> maybeMetadata =
                            Optional(episode.Season?.Show?.ShowMetadata.HeadOrNone()).Flatten();
                        if (maybeMetadata.IsSome)
                        {
                            ShowMetadata metadata = maybeMetadata.ValueUnsafe();
                            string poster = Optional(metadata.Artwork).Flatten()
                                .Filter(a => a.ArtworkKind == ArtworkKind.Poster)
                                .HeadOrNone()
                                .Match(
                                    artwork => $"{_scheme}://{_host}/artwork/posters/{artwork.Path}",
                                    () => string.Empty);

                            if (!string.IsNullOrWhiteSpace(poster))
                            {
                                xml.WriteStartElement("icon");
                                xml.WriteAttributeString("src", poster);
                                xml.WriteEndElement(); // icon
                            }
                        }

                        int s = Optional(episode.Season?.SeasonNumber).IfNone(0);
                        int e = episode.EpisodeNumber;
                        if (s > 0 && e > 0)
                        {
                            xml.WriteStartElement("episode-num");
                            xml.WriteAttributeString("system", "onscreen");
                            xml.WriteString($"S{s:00}E{e:00}");
                            xml.WriteEndElement(); // episode-num

                            xml.WriteStartElement("episode-num");
                            xml.WriteAttributeString("system", "xmltv_ns");
                            xml.WriteString($"{s - 1}.{e - 1}.0/1");
                            xml.WriteEndElement(); // episode-num
                        }
                    }

                    // sb.AppendLine("<icon src=\"\"/>");

                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        xml.WriteStartElement("desc");
                        xml.WriteAttributeString("lang", "en");
                        xml.WriteString(description);
                        xml.WriteEndElement(); // desc
                    }

                    if (!string.IsNullOrWhiteSpace(contentRating))
                    {
                        xml.WriteStartElement("rating");
                        xml.WriteAttributeString("system", "MPAA");
                        xml.WriteStartElement("value");
                        xml.WriteString(contentRating);
                        xml.WriteEndElement(); // value
                        xml.WriteEndElement(); // rating
                    }

                    xml.WriteEndElement(); // programme
                }
            }

            xml.WriteEndElement(); // tv
            xml.WriteEndDocument();

            xml.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}
