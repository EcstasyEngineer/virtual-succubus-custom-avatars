using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.Serialization.Json;
using EcstasyEngineer.AvatarReplacement;

internal static class Program
{
    private static int count;
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; }
    private static void Reject(Action action, string label) { try { action(); } catch (InvalidDataException) { count++; return; } throw new Exception(label); }
    private static void Main()
    {
        var digest=new string('a',64);
        var profile=new FacialProfile { schemaVersion=1,modelId="ani-01-classic",mapping="ani-arkit-v1",jawPolicy="prefab-rest",bundleSha256=digest };
        FacialProfile.Validate(profile,digest.ToUpperInvariant()); count++;
        Reject(()=>FacialProfile.Validate(profile,new string('b',64)),"stale bundle profile accepted");
        profile.modelId="unverified"; Reject(()=>FacialProfile.Validate(profile,digest),"unknown model accepted"); profile.modelId="ani-01-classic";
        profile.jawPolicy="guess"; Reject(()=>FacialProfile.Validate(profile,digest),"unknown jaw policy accepted"); profile.jawPolicy="prefab-rest";
        profile.schemaVersion=2; Reject(()=>FacialProfile.Validate(profile,digest),"unknown schema accepted"); profile.schemaVersion=1;
        profile.mapping="other"; Reject(()=>FacialProfile.Validate(profile,digest),"unknown map accepted"); profile.mapping="ani-arkit-v1";
        profile.bundleSha256=digest+"\n"; Reject(()=>FacialProfile.Validate(profile,digest),"trailing digest newline accepted");
        var scratch=Path.Combine(Path.GetTempPath(),"avatar-profile-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var bundle=Path.Combine(scratch,"fixture.bundle"); var sidecar=bundle+".face.json";
        try {
            var bytes=Encoding.ASCII.GetBytes("non-executable hash-pair fixture"); File.WriteAllBytes(bundle,bytes);
            Reject(()=>FacialProfile.VerifyBundle(bundle),"missing sidecar accepted");
            profile.bundleSha256=Convert.ToHexString(SHA256.HashData(bytes));
            using(var stream=File.Create(sidecar))new DataContractJsonSerializer(typeof(FacialProfile)).WriteObject(stream,profile);
            Check(FacialProfile.VerifyBundle(bundle)=="ani-01-classic","serialized hash-paired sidecar rejected");
            File.AppendAllText(bundle,"changed"); Reject(()=>FacialProfile.VerifyBundle(bundle),"changed actual bundle bytes accepted");
            File.WriteAllText(sidecar,new string(' ',4097)); Reject(()=>FacialProfile.VerifyBundle(bundle),"oversize sidecar accepted");
        } finally { File.Delete(sidecar); File.Delete(bundle); Directory.Delete(scratch); }
        Console.WriteLine(count+" facial-profile assertions passed. Real hash-pair validation; no Unity runtime called.");
    }
}
