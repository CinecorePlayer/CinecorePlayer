#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CinecorePlayer2025.Utilities
{
    // Testi composti al momento (numeri, nomi): inglese con {0}, {1}... <tab> spagnolo con gli stessi segnaposto.
    // Il testo a schermo si confronta con il modello inglese e i valori passano nella frase spagnola.
    internal static partial class SpanishTexts
    {
        private static readonly string TemplateData =
            @"   {0} min left	   quedan {0} min
 ({0} failed)	 ({0} con error)
, about {0} left	, quedan unos {0}
Album with {0} tracks. Open it to choose a track.	Álbum con {0} pistas. Ábrelo para elegir una pista.
Already connected to {0}.	Ya conectado a {0}.
Analysing {0} tracks for mixing…	Analizando {0} pistas para la mezcla…
Audio aligned: {0} ms	Audio alineado: {0} ms
Audio delay: {0} ms	Retraso del audio: {0} ms
Automatic headroom −{0} dB: no sample above the ceiling.	Margen automático −{0} dB: ninguna muestra por encima del techo.
Average {0}/5	Media {0}/5
Choose a folder for {0}.	Elige una carpeta para {0}.
Connected, volume {0}. Link the audio output and save.	Conectado, volumen {0}. Vincula la salida de audio y guarda.
Connecting to {0}...	Conectando con {0}...
Crossfade between tracks: {0} s	Fundido entre pistas: {0} s
Different speed: {0}%{1}, a converted copy is needed	Velocidad distinta: {0}%{1}, hace falta una copia convertida
In-depth analysis {0}/{1}	Análisis a fondo {0}/{1}
Loading {0}...	Cargando {0}...
Manual, {0}, not verified	Manual, {0}, sin verificar
Max boost +{0} dB · protection off: clipping possible.	Refuerzo máximo +{0} dB · protección desactivada: posible saturación.
Measured at {0} points: the player follows it by itself while watching.	Medido en {0} puntos: el reproductor lo sigue solo mientras ves la película.
Needs checking, {0}	Hay que comprobarlo, {0}
Opening stream ({0})…	Abriendo la transmisión ({0})…
Playing ({0} • {1})	Reproduciendo ({0} • {1})
Receiver: {0} → {1}	Receptor: {0} → {1}
Results for “{0}”	Resultados de «{0}»
Season with {0} episodes. Open it to choose an episode and view TMDb details.	Temporada con {0} episodios. Ábrela para elegir un episodio y ver los datos de TMDb.
Season {0}	Temporada {0}
Server not ready yet. Retrying {0}/{1}…	El servidor aún no está listo. Reintento {0}/{1}…
Show {0} more	Mostrar {0} más
Subtitles: aligning the timings to the audio… {0}%	Subtítulos: alineando los tiempos con el audio… {0}%
Subtitles: {0}	Subtítulos: {0}
TMDb metadata {0}/{1}	Metadatos de TMDb {0}/{1}
The middle value is suggested: up to {0} ms remain in some scenes.	Se sugiere el valor intermedio: en algunas escenas quedan hasta {0} ms.
The track is {0}% {1}{2}: the offset changes by {3} s per minute and no fixed delay can align it. {4} It can be converted to the video's speed.	La pista va un {0}% {1}{2}: el desfase cambia {3} s por minuto y ningún retraso fijo puede alinearla. {4} Se puede convertir a la velocidad del vídeo.
The track was synchronised in sections: the delay goes from {0} to {1} ms along the film.	La pista se sincronizó por tramos: el retraso va de {0} a {1} ms a lo largo de la película.
The tracks are not the same edit: {0}. One delay aligns only one stretch; the one of the longest stretch is suggested.	Las pistas no son del mismo montaje: {0}. Un retraso alinea solo un tramo; se sugiere el del tramo más largo.
The two tracks are not the same edit: the delay goes from {0} to {1} ms at {2} points.	Las dos pistas no son del mismo montaje: el retraso va de {0} a {1} ms en {2} puntos.
Track converted in {0}, delay {1} ms, press Apply	Pista convertida en {0}, retraso {1} ms, pulsa Aplicar
Track {0}	Pista {0}
Unable to present video with {0}	No se puede mostrar el vídeo con {0}
Verified, {0}, confidence {1}	Verificado, {0}, confianza {1}
Volume for this film: {0}%	Volumen de esta película: {0}%
from {0} to {1} delay {2} ms	de {0} a {1} retraso {2} ms
libmpv is already loaded as {0}. To switch to {1}, close and reopen Cinecore Player.	libmpv ya está cargado como {0}. Para cambiar a {1}, cierra y vuelve a abrir Cinecore Player.
variable, from {0} to {1} ms	variable, de {0} a {1} ms
varies by ±{0} ms between scenes	varía ±{0} ms entre escenas
{0} TV shows	{0} series
{0} albums   {1} tracks	{0} álbumes   {1} pistas
{0} artists	{0} artistas
{0} color	Color {0}
{0} color updated	Color {0} actualizado
{0} devices found. Test the selected one.	{0} dispositivos encontrados. Prueba el seleccionado.
{0} five-star	{0} de cinco estrellas
{0} items found, still scanning…	{0} elementos encontrados, sigue la búsqueda…
{0} items loaded from {1}.	{0} elementos cargados desde {1}.
{0} items will be added	Se añadirán {0} elementos
{0} left	Quedan {0}
{0} of {1} scenes agree within {2} ms.	{0} de {1} escenas coinciden en {2} ms.
{0} of {1} scenes found, but the delay changes by {2} ms from one to another: the track was synchronised in sections and no single value fits them all.	Se encontraron {0} de {1} escenas, pero el retraso cambia {2} ms de una a otra: la pista se sincronizó por tramos y ningún valor único sirve para todas.
{0} playlists	{0} listas
{0} points of the film agree within {1} ms. Constant offset verified.	{0} puntos de la película coinciden en {1} ms. Desfase constante verificado.
{0} remaining	Quedan {0}
{0} seasons	{0} temporadas
{0} shared words across the scene. Check the result: different languages or edits may need further correction.	{0} palabras en común en la escena. Comprueba el resultado: idiomas o montajes distintos pueden necesitar más corrección.
{0} titles	{0} títulos
{0} videos	{0} vídeos
{0} watched	{0} vistos
{0}% - scanning: {1}{2}	{0}% - analizando: {1}{2}
{0}% - {1}/{2} seasons complete{3}	{0}% - {1}/{2} temporadas completas{3}
{0}% - {1}/{2} seasons, {3} queued{4}	{0}% - {1}/{2} temporadas, {3} en cola{4}
{0}/{1} tracks	{0}/{1} pistas";

        private static List<(Regex English, string Spanish)>? _templates;
        private static readonly ConcurrentDictionary<string, string> TemplateCache = new(StringComparer.Ordinal);

        /// <summary>Traduzione di un testo composto, o null se nessun modello lo riconosce.</summary>
        private static string? FromTemplate(string english)
        {
            if (english.Length > 400) return null;
            if (TemplateCache.TryGetValue(english, out string? cached)) return cached.Length == 0 ? null : cached;
            _templates ??= BuildTemplates();
            string result = "";
            foreach (var (pattern, spanish) in _templates)
            {
                var match = pattern.Match(english);
                if (!match.Success) continue;
                result = Regex.Replace(spanish, @"\{(\d)\}", m =>
                {
                    int index = m.Groups[1].Value[0] - '0' + 1;
                    return index < match.Groups.Count ? match.Groups[index].Value : m.Value;
                });
                break;
            }
            if (TemplateCache.Count < 4000) TemplateCache[english] = result;
            return result.Length == 0 ? null : result;
        }

        private static List<(Regex, string)> BuildTemplates()
        {
            var list = new List<(Regex, string)>();
            foreach (string raw in TemplateData.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int tab = line.IndexOf('\t');
                if (tab <= 0 || tab >= line.Length - 1) continue;
                string english = line[..tab], spanish = line[(tab + 1)..];
                // I segnaposto diventano gruppi; il resto deve combaciare alla lettera.
                string pattern = "^" + Regex.Replace(Regex.Escape(english), @"\\\{(\d)}", "(.*?)") + "$";
                list.Add((new Regex(pattern, RegexOptions.CultureInvariant), spanish));
            }
            return list;
        }
    }
}
