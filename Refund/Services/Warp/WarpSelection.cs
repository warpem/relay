using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Warp.Tools;
using Warp;

namespace Refund.Services.Warp;
public sealed partial class WarpSession
{
    public double MinCtf=>_filters.MinCtf;public double MaxCtf=>_filters.MaxCtf;
    /// <summary>The accepted CTF signal scores; unmeasured scores pass. A null maximum has no upper limit.</summary>
    public double MinCtfSignal=>_filters.MinCtfSignal;public double? MaxCtfSignal=>_filters.MaxCtfSignal;
    public double MinMotion=>_filters.MinMotion;public double MaxMotion=>_filters.MaxMotion;
    public double MinIce=>_filters.MinIce;public double MaxIce=>_filters.MaxIce;
    public int MinParticles=>_filters.MinParticles;public int MaxParticles=>_filters.MaxParticles;
    public double MaxAstigmatismSigma=>_filters.MaxAstigmatismSigma;
    public void FilterMetric(string metric,double value)
    {
        if(!double.IsFinite(value))return;
        Mutate(()=>_filters=metric switch{
            "ctf-min"=>_filters with{MinCtf=Math.Clamp(value,0,MaxCtf)},"ctf" or "ctf-max"=>_filters with{MaxCtf=Math.Max(MinCtf,value)},
            "ctf-signal" or "ctf-signal-min"=>_filters with{MinCtfSignal=Math.Clamp(value,0,MaxCtfSignal??double.MaxValue)},
            "ctf-signal-max"=>_filters with{MaxCtfSignal=Math.Max(MinCtfSignal,value)},
            "motion-min"=>_filters with{MinMotion=Math.Clamp(value,0,MaxMotion)},"motion" or "motion-max"=>_filters with{MaxMotion=Math.Max(MinMotion,value)},
            "ice-min"=>_filters with{MinIce=Math.Clamp(value,0,MaxIce)},"ice" or "ice-max"=>_filters with{MaxIce=Math.Max(MinIce,value)},
            "particles" or "particles-min"=>_filters with{MinParticles=(int)Math.Clamp(value,0,MaxParticles)},"particles-max"=>_filters with{MaxParticles=(int)Math.Clamp(value,MinParticles,int.MaxValue)},
            "astigmatism"=>_filters with{MaxAstigmatismSigma=Math.Max(0,value)},_=>_filters});
    }
    /// <summary>Sets the CTF signal's upper limit; null removes it (the field's empty state).</summary>
    public void FilterCtfSignalMaximum(double? value)
    {
        if(value.HasValue&&!double.IsFinite(value.Value))return;
        Mutate(()=>_filters=_filters with{MaxCtfSignal=value.HasValue?Math.Max(MinCtfSignal,value.Value):null});
    }
    /// <summary>The movie's picks are from the current settings (later stages may still be redone, e.g. for new score bounds).</summary>
    public bool HasCurrentParticleMetrics(WarpMovie m)=>HistogramEligible(m)&&File.Exists(m.ParticleStarPath);
    /// <summary>The movie's extraction is current, so its extracted particles are what the session exports.</summary>
    public bool HasCurrentExtraction(WarpMovie m)=>!m.Failed&&m.Done.HasFlag(WarpStage.Extract)&&!m.Stale.HasFlag(WarpStage.Extract);
    // Only the aggregate is persisted. Membership is reconstructed from the compact catalog.
    private readonly Dictionary<int,long> _histogramMembers=[];
    private void InvalidateHistogram() { _histogramCohort=""; }
    private void ResetHistogram()
    {
        _histogramMembers.Clear();_histogram=new(new int[64*48],64,48,1,0,0);_histogramCohort="";
    }
    private void RemoveHistogramMovie(WarpMovie movie)
    {
        if(!_histogramMembers.Remove(movie.Index)||_histogram==null)return;
        try
        {
            var bins=(int[])_histogram.Bins.Clone();var particles=ReadParticles(movie.ParticleStarPath);
            foreach(var p in particles){int x=Math.Clamp((int)(p.Correlation*64),0,63),y=Math.Clamp((int)(p.Power/_histogram.PowerMaximum*48),0,47);bins[y*64+x]=Math.Max(0,bins[y*64+x]-1);}
            _histogram=_histogram with{Bins=bins,Total=bins.Sum(v=>(long)v),Movies=_histogramMembers.Count};_histogramCohort="";
        }
        catch(Exception ex) when(ex is IOException or FormatException or InvalidDataException){ResetHistogram();_logger.Warning(ex,"Rebuilding particle histogram after missing departing result");}
    }
    private string ParticleCohortSignature()
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes=stackalloc byte[256];
        foreach(var m in _movies)if(HistogramEligible(m))
        {
            int length=Encoding.UTF8.GetBytes(m.Id.AsSpan(),bytes);hash.AppendData(bytes[..length]);
            BitConverter.TryWriteBytes(bytes,m.PickingRevision);hash.AppendData(bytes[..8]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    // Rendering and dragging the score bounds never read STAR files: the histogram aggregates every current pick.
    public WarpParticleHistogram GetParticleHistogram()
    {
        lock(_sync)return _histogram??=new(new int[64*48],64,48,1,0,0);
    }
    // At most four newly eligible movies per coordinator/checkpoint pass. Reads happen
    // outside the catalog lock; a revision check discards superseded results.
    private void UpdateHistogramBatch()
    {
        WarpMovie[] pending;
        lock(_sync)
        {
            foreach(int index in _histogramMembers.Where(pair=>!HistogramEligible(_movies[pair.Key])||_movies[pair.Key].PickingRevision!=pair.Value).Select(pair=>pair.Key).ToArray())RemoveHistogramMovie(_movies[index]);
            pending=_movies.Where(m=>HistogramEligible(m)&&!_histogramMembers.ContainsKey(m.Index)).Take(4).Select(m=>m with{}).ToArray();
        }
        foreach(var movie in pending)
        {
            WarpParticle[] particles;
            try{particles=ReadParticles(movie.ParticleStarPath);}
            catch(Exception ex) when(ex is IOException or FormatException or InvalidDataException){_logger.Warning(ex,"Cannot add particle scores for {Movie}",movie.Name);continue;}
            lock(_sync)
            {
                var current=_movies[movie.Index];
                if(!HistogramEligible(current)||current.PickingRevision!=movie.PickingRevision||_histogramMembers.ContainsKey(movie.Index))continue;
                _histogram??=new(new int[64*48],64,48,1,0,0);
                var bins=(int[])_histogram.Bins.Clone();double maximum=_histogram.PowerMaximum;
                double incoming=particles.Length==0?0:particles.Max(p=>p.Power);
                while(incoming>=maximum)
                {
                    var merged=new int[64*48];
                    for(int y=0;y<48;y++)for(int x=0;x<64;x++)merged[(y/2)*64+x]+=bins[y*64+x];
                    bins=merged;maximum*=2;
                }
                foreach(var particle in particles){int x=Math.Clamp((int)(particle.Correlation*64),0,63),y=Math.Clamp((int)(particle.Power/maximum*48),0,47);bins[y*64+x]++;}
                _histogramMembers[movie.Index]=movie.PickingRevision;
                _histogram=new(bins,64,48,maximum,_histogram.Total+particles.LongLength,_histogramMembers.Count);
                _histogramCohort=_histogramMembers.Count==_movies.Count(HistogramEligible)?ParticleCohortSignature():"";
                GetParticleHistogram();Save();
            }
        }
    }
    private static bool HistogramEligible(WarpMovie m)=>!m.Failed&&m.Done.HasFlag(WarpStage.Pick)&&!m.Stale.HasFlag(WarpStage.Pick);
    private WarpParticle[] GetParticles(WarpMovie m)
    {
        if(_particleCache.TryGetValue(m.Index,out var entry)&&entry.Revision==m.PickingRevision)return entry.Particles;
        var particles=ReadParticles(m.ParticleStarPath);
        if(particles.Length<=16000)
        {
            while(_particleCache.Count>=8&&_particleCacheOrder.TryDequeue(out int old))_particleCache.Remove(old);
            _particleCache[m.Index]=(m.PickingRevision,particles);_particleCacheOrder.Enqueue(m.Index);
        }
        return particles;
    }
    internal static WarpParticle[] ReadParticles(string path)
    {
        var table=Star.ContainsTable(path,"particles")?new Star(path,"particles"):new Star(path);
        string[] x=table.GetColumn("rlnCoordinateX"),y=table.GetColumn("rlnCoordinateY"),correlation=table.GetColumn("rlnAutopickFigureOfMerit"),power=table.GetColumn("wrpAutopickPower");
        if(x==null||y==null||correlation==null||power==null)throw new InvalidDataException("Particle STAR requires coordinates, Pearson correlation and local power columns.");
        var result=new WarpParticle[x.Length];
        for(int i=0;i<result.Length;i++)
        {
            double px=double.Parse(x[i],CultureInfo.InvariantCulture),py=double.Parse(y[i],CultureInfo.InvariantCulture),c=double.Parse(correlation[i],CultureInfo.InvariantCulture),p=double.Parse(power[i],CultureInfo.InvariantCulture);
            if(!double.IsFinite(px)||!double.IsFinite(py)||!double.IsFinite(c)||!double.IsFinite(p)||p<0)throw new InvalidDataException("Particle STAR contains invalid coordinates or scores.");
            result[i]=new(px,py,c,p,i);
        }
        return result;
    }
    /// <summary>
    /// The movie's current picks within <paramref name="bounds"/>, as a STAR overlay in the pixels of the displayed image
    /// (default: the image picking ran on); null without current picks. Used to try bounds before applying them.
    /// </summary>
    public string GetPicksWithinStar(int movieIndex,WarpParticleBounds bounds,double? displayPixelSize=null)
    {
        lock(_sync)
        {
            if(movieIndex<0||movieIndex>=_movies.Count)return null;
            var m=_movies[movieIndex];if(!HasCurrentParticleMetrics(m))return null;
            return WriteOverlay(m,"picks",Fingerprint(new{m.PickingRevision,bounds}).ToString("x16"),GetParticles(m).Where(bounds.Contains),displayPixelSize);
        }
    }
    /// <summary>How many of the movie's current picks lie within <paramref name="bounds"/>; null without current picks.</summary>
    public int? CountPicksWithin(int movieIndex,WarpParticleBounds bounds)
    {
        lock(_sync)
        {
            if(movieIndex<0||movieIndex>=_movies.Count||!HasCurrentParticleMetrics(_movies[movieIndex]))return null;
            try{return GetParticles(_movies[movieIndex]).Count(bounds.Contains);}
            catch(Exception ex) when(ex is IOException or FormatException or InvalidDataException){return null;}
        }
    }
    /// <summary>
    /// The particles the movie's current extraction kept, as a STAR overlay in the pixels of the displayed image; null
    /// without a current extraction or when it kept none.
    /// </summary>
    public string GetExtractedParticleStar(int movieIndex,double? displayPixelSize=null)
    {
        lock(_sync)
        {
            if(movieIndex<0||movieIndex>=_movies.Count)return null;
            var m=_movies[movieIndex];if(!HasCurrentExtraction(m)||m.Particles==0||!File.Exists(m.ExtractedStarPath))return null;
            try{return WriteOverlay(m,"extracted",Fingerprint(new{m.ExtractionRevision,m.Particles}).ToString("x16"),ReadParticles(m.ExtractedStarPath),displayPixelSize);}
            catch(Exception ex) when(ex is FormatException or InvalidDataException){_logger.Warning(ex,"Cannot show extracted particles for {Movie}",m.Name);return null;}
        }
    }
    private string WriteOverlay(WarpMovie m,string kind,string key,IEnumerable<WarpParticle> particles,double? displayPixelSize)
    {
        double pixel=displayPixelSize??m.EnhancedPixelSize;if(!double.IsFinite(pixel)||pixel<=0)throw new ArgumentOutOfRangeException(nameof(displayPixelSize));
        key=Fingerprint(new{key,pixel}).ToString("x16");
        string directory=Path.Combine(Identity.SessionDirectory,"overlays");Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,$"{m.Id}-{kind}-{key}.star");if(File.Exists(path))return path;
        string temporary=path+".writing";
        using(var writer=new StreamWriter(temporary))
        {
            writer.WriteLine("data_particles\n\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n_rlnAutopickFigureOfMerit #3\n_wrpAutopickPower #4");
            double scale=m.EnhancedPixelSize/pixel;
            foreach(var p in particles)writer.WriteLine(FormattableString.Invariant($"{p.X*scale:G9} {p.Y*scale:G9} {p.Correlation:G9} {p.Power:G9}"));
        }
        File.Move(temporary,path,true);
        return path;
    }
}
