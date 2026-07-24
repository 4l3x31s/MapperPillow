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

// Collections map with the same zero-ceremony call.
var users = new List<User>
{
    new() { Id = 1, Name = "Grace Hopper", Email = "grace@navy.mil" },
    new() { Id = 2, Name = "Alan Turing", Email = "alan@bletchley.uk" },
};

List<UserDto> dtos = users.MapTo<List<UserDto>>();
Console.WriteLine($"  mapped {dtos.Count} users: {string.Join(", ", dtos.Select(u => u.Name))}");

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
