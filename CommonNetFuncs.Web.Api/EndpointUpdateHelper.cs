using System.ComponentModel.DataAnnotations;
using CommonNetFuncs.EFCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using static CommonNetFuncs.Core.Copy;
using static CommonNetFuncs.Core.ExceptionLocation;
using static CommonNetFuncs.DeepClone.ExpressionTrees;

namespace CommonNetFuncs.Web.Api;

internal enum EntityUpdateStatus
{
	NoContent,
	Ok,
	ValidationFailed,
}

internal readonly record struct EntityUpdateResult<TOut>(EntityUpdateStatus Status, TOut? Value = default, Dictionary<string, string[]>? Errors = null);

// Update/patch flow shared by the MVC and minimal-API endpoint classes, which only differ in how they shape the response.
internal static class EndpointUpdateHelper
{
	private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

	internal static async Task<EntityUpdateResult<TOut>> UpdateAndSave<TModel, TContext, TOut>(TModel? dbModel, bool hasChanges, Action<TModel> applyChanges,
		IBaseDbContextActions<TModel, TContext> baseAppDbContextActions, Func<TModel, TOut?> map) where TModel : class?, new() where TContext : DbContext
	{
		try
		{
			if (dbModel == null)
			{
				return new(EntityUpdateStatus.NoContent);
			}

			if (!hasChanges)
			{
				return new(EntityUpdateStatus.Ok, map(dbModel));
			}

			TModel updateModel = dbModel.DeepClone();
			applyChanges(updateModel);

			List<ValidationResult> failedValidations = [];
			if (!Validator.TryValidateObject(updateModel, new(updateModel), failedValidations))
			{
				return new(EntityUpdateStatus.ValidationFailed, Errors: failedValidations.ToDictionary(x => x.MemberNames.FirstOrDefault() ?? "Error", x => new string[] { x.ErrorMessage! }));
			}

			updateModel.CopyPropertiesTo(dbModel);
			baseAppDbContextActions.Update(dbModel);
			if (await baseAppDbContextActions.SaveChanges().ConfigureAwait(false))
			{
				return new(EntityUpdateStatus.Ok, map(dbModel));
			}
		}
		catch (Exception ex)
		{
			logger.Error(ex, ErrorLocationTemplate, ex.GetLocationOfException());
		}
		return new(EntityUpdateStatus.NoContent);
	}

	internal static Results<Ok<TOut>, NoContent, ValidationProblem> ToTypedResult<TOut>(EntityUpdateResult<TOut> result)
	{
		return result.Status switch
		{
			EntityUpdateStatus.Ok => TypedResults.Ok(result.Value),
			EntityUpdateStatus.ValidationFailed => TypedResults.ValidationProblem(result.Errors!),
			_ => TypedResults.NoContent()
		};
	}
}
