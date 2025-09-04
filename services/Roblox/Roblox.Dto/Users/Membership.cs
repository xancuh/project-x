using Roblox.Models.Users;

namespace Roblox.Dto.Users;

public class UserMembershipEntry
{
    public long userId { get; set; }
    public MembershipType membershipType { get; set; }
    public DateTime createdAt { get; set; }
    public DateTime updatedAt { get; set; }
}

public class MembershipMetadata
{
    private static List<MembershipMetadata> _membershipMetadata = new()
    {
        new MembershipMetadata(MembershipType.None, "None", 0),
        new MembershipMetadata(MembershipType.BuildersClub, "Builders Club", 30),
        new MembershipMetadata(MembershipType.TurboBuildersClub, "Turbo Builders Club", 70),
        new MembershipMetadata(MembershipType.OutrageousBuildersClub, "Outrageous Builders Club", 100),
    };

    public static MembershipMetadata GetMetadata(MembershipType membershipType)
    {
        var result = _membershipMetadata.Find(v => v.membershipType == membershipType);
        if (result == null)
            throw new ArgumentException("Invalid " + nameof(membershipType));
        return result;
    }

    public MembershipMetadata(MembershipType type, string displayName, long dailyRobux)
    {
        this.membershipType = type;
        this.dailyRobux = dailyRobux;
        this.displayName = displayName;
    }
    
    public long dailyRobux { get; set; }
    public MembershipType membershipType { get; set; }
    public string displayName { get; set; }
}