using MapperPillow;

var user = new User
{
    Id = 7,
    Name = "Ada Lovelace",
    Email = "ada@calculus.dev",
};

// Zero ceremony: no mapper instance, no DI, no configuration.
UserDto dto = user.MapTo<UserDto>();

Console.WriteLine("MapperPillow sample");
Console.WriteLine($"  Id:    {dto.Id}");
Console.WriteLine($"  Name:  {dto.Name}");
Console.WriteLine($"  Email: {dto.Email}");

public sealed class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

public sealed class UserDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}
