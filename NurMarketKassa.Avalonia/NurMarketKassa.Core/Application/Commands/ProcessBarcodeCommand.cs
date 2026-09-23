using MediatR;

namespace NurMarketKassa.Core.Application.Commands;

public sealed record ProcessBarcodeCommand(string Barcode) : IRequest<bool>;
