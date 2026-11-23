namespace Streaik.Gateway;

/// <summary>Describes a manually parsed request body without participating in request matching or binding.</summary>
public sealed record GatewayRequestBodyMetadata(Type? JsonType, GatewayMultipartBody? Multipart = null);

public enum GatewayMultipartBody { ImageEdit, AudioTranscription, SkillUpload }
