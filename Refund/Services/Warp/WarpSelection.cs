using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Warp.Tools;
using Warp;

namespace Refund.Services.Warp;
public sealed partial class WarpSession
{
    public double MinCtf=>_filters.MinCtf;public double MaxCtf=>_filters.MaxCtf;
    public double MinMotion=>_filters.MinMotion;public double MaxMotion=>_filters.MaxMotion;
    public double MinIce=>_filters.MinIce;public double MaxIce=>_filters.MaxIce;
    public int MinParticles=>_filters.MinParticles;public int MaxParticles=>_filters.MaxParticles;
    public double MaxAstigmatismSigma=>_filters.MaxAstigmatismSigma;
    public double MinPickCorrelation=>_filters.MinPickCorrelation;public double MaxPickCorrelation=>_filters.MaxPickCorrelation;
    public double MinPickPower=>_filters.MinPickPower;public double? MaxPickPower=>_filters.MaxPickPower;
    public void FilterMetric(string metric,double value)
    {
        if(!double.IsFinite(value))return;
        Mutate(()=>_filters=metric switch{
            "ctf-min"=>_filters with{MinCtf=Math.Clamp(value,0,MaxCtf)},"ctf" or "ctf-max"=>_filters with{MaxCtf=Math.Max(MinCtf,value)},
            "motion-min"=>_filters with{MinMotion=Math.Clamp(value,0,MaxMotion)},"motion" or "motion-max"=>_filters with{MaxMotion=Math.Max(MinMotion,value)},
            "ice-min"=>_filters with{MinIce=Math.Clamp(value,0,MaxIce)},"ice" or "ice-max"=>_filters with{MaxIce=Math.Max(MinIce,value)},
            "particles" or "particles-min"=>_filters with{MinParticles=(int)Math.Clamp(value,0,MaxParticles)},"particles-max"=>_filters with{MaxParticles=(int)Math.Clamp(value,MinParticles,int.MaxValue)},
            "astigmatism"=>_filters with{MaxAstigmatismSigma=Math.Max(0,value)},_=>_filters});
    }
    public void SetParticleRange(double minCorrelation,double maxCorrelation,double minPower,double? maxPower)
    {
        if(!double.IsFinite(minCorrelation)||!double.IsFinite(maxCorrelation)||!double.IsFinite(minPower)||maxPower.HasValue&&!double.IsFinite(maxPower.Value))return;
        Mutate(()=>{double min=Math.Clamp(minCorrelation,0,1),power=Math.Max(0,minPower);_filters=_filters with{MinPickCorrelation=min,MaxPickCorrelation=Math.Clamp(maxCorrelation,min,1),MinPickPower=power,MaxPickPower=maxPower.HasValue?Math.Max(power,maxPower.Value):null};_histogramFilter="";});
    }
    public void FilterParticleMetric(string metric,double? value)
    {
        if(value.HasValue&&!double.IsFinite(value.Value))return;
        switch(metric){case "correlation-min":SetParticleRange(Math.Clamp(value??0,0,MaxPickCorrelation),MaxPickCorrelation,MinPickPower,MaxPickPower);break;
            case "correlation-max":SetParticleRange(MinPickCorrelation,value??1,MinPickPower,MaxPickPower);break;
            case "power-min":SetParticleRange(MinPickCorrelation,MaxPickCorrelation,Math.Clamp(value??0,0,MaxPickPower??double.MaxValue),MaxPickPower);break;
            case "power-max":SetParticleRange(MinPickCorrelation,MaxPickCorrelation,MinPickPower,value);break;}
    }
    public bool HasCurrentParticleMetrics(WarpMovie m)=>!m.Failed&&m.Done.HasFlag(WarpStage.Pick)&&m.Stale==WarpStage.None&&File.Exists(m.ParticleStarPath);
    public bool PassesParticleFilter(WarpParticle p){var f=_filters;return p.Correlation>=f.MinPickCorrelation&&p.Correlation<=f.MaxPickCorrelation&&p.Power>=f.MinPickPower&&(!f.MaxPickPower.HasValue||p.Power<=f.MaxPickPower);}
    // Only the aggregate is persisted. Membership is reconstructed from the compact catalog.
    private readonly Dictionary<int,long> _histogramMembers=[];
    private void InvalidateHistogram() { _histogramCohort=""; }
    private void ResetHistogram()
    {
        _histogramMembers.Clear();_histogram=new(new int[64*48],64,48,1,0,0,0);_histogramCohort="";_histogramFilter="";
    }
    private void RemoveHistogramMovie(WarpMovie movie)
    {
        if(!_histogramMembers.Remove(movie.Index)||_histogram==null)return;
        try
        {
            var bins=(int[])_histogram.Bins.Clone();var particles=ReadParticles(movie.ParticleStarPath);
            foreach(var p in particles){int x=Math.Clamp((int)(p.Correlation*64),0,63),y=Math.Clamp((int)(p.Power/_histogram.PowerMaximum*48),0,47);bins[y*64+x]=Math.Max(0,bins[y*64+x]-1);}
            _histogram=_histogram with{Bins=bins,Total=bins.Sum(v=>(long)v),Movies=_histogramMembers.Count};_histogramFilter="";_histogramCohort="";
        }
        catch(Exception ex) when(ex is IOException or FormatException or InvalidDataException){ResetHistogram();_logger.Warning(ex,"Rebuilding particle histogram after missing departing result");}
    }
    private string ParticleFilterSignature()=>Fingerprint(new{MinPickCorrelation,MaxPickCorrelation,MinPickPower,MaxPickPower}).ToString("x16");
    private string ParticleCohortSignature()
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes=stackalloc byte[256];
        foreach(var m in _movies)if(!m.Failed&&m.Done.HasFlag(WarpStage.Pick)&&m.Stale==0)
        {
            int length=Encoding.UTF8.GetBytes(m.Id.AsSpan(),bytes);hash.AppendData(bytes[..length]);
            BitConverter.TryWriteBytes(bytes,m.PickingRevision);hash.AppendData(bytes[..8]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    // Rendering and dragging filters never read STAR files. This count estimates the
    // selection from bin centres; export applies exact per-particle limits.
    public WarpParticleHistogram GetParticleHistogram()
    {
        lock(_sync)
        {
            _histogram??=new(new int[64*48],64,48,1,0,0,0);
            string signature=ParticleFilterSignature();
            if(_histogramFilter!=signature)
            {
                long accepted=0;
                for(int y=0;y<48;y++)for(int x=0;x<64;x++)
                    {
                        double correlation=(x+.5)/64,power=(y+.5)*_histogram.PowerMaximum/48;
                        if(correlation>=MinPickCorrelation&&correlation<=MaxPickCorrelation&&power>=MinPickPower&&(!MaxPickPower.HasValue||power<=MaxPickPower.Value))accepted+=_histogram.Bins[y*64+x];
                    }
                _histogram=_histogram with{Accepted=accepted};_histogramFilter=signature;
            }
            return _histogram;
        }
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
                _histogram??=new(new int[64*48],64,48,1,0,0,0);
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
                _histogram=new(bins,64,48,maximum,_histogram.Total+particles.LongLength,0,_histogramMembers.Count);_histogramFilter="";
                _histogramCohort=_histogramMembers.Count==_movies.Count(HistogramEligible)?ParticleCohortSignature():"";
                GetParticleHistogram();Save();
            }
        }
    }
    private static bool HistogramEligible(WarpMovie m)=>!m.Failed&&m.Done.HasFlag(WarpStage.Pick)&&m.Stale==0;
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
    public string GetFilteredParticleStar(int movieIndex,double? displayPixelSize=null)
    {
        lock(_sync)
        {
            if(movieIndex<0||movieIndex>=_movies.Count)return null;
            var m=_movies[movieIndex];if(!HasCurrentParticleMetrics(m))return null;
            double pixel=displayPixelSize??m.EnhancedPixelSize;if(!double.IsFinite(pixel)||pixel<=0)throw new ArgumentOutOfRangeException(nameof(displayPixelSize));
            string key=Fingerprint(new{m.PickingRevision,Filter=ParticleFilterSignature(),pixel}).ToString("x16");
            string directory=Path.Combine(Identity.SessionDirectory,"overlays");Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,m.Id+"-"+key+".star");if(File.Exists(path))return path;
            using var writer=new StreamWriter(path);writer.WriteLine("data_particles\n\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n_rlnAutopickFigureOfMerit #3\n_wrpAutopickPower #4");
            double scale=m.EnhancedPixelSize/pixel;
            foreach(var p in GetParticles(m))if(PassesParticleFilter(p))writer.WriteLine(FormattableString.Invariant($"{p.X*scale:G9} {p.Y*scale:G9} {p.Correlation:G9} {p.Power:G9}"));
            return path;
        }
    }
}
