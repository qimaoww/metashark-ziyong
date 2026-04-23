using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class MetaSharkSharedEntityLibraryCapabilityResolverTest
    {
        private const BindingFlags InstanceMemberBindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [TestMethod]
        public void Resolve_PersonLinkedOnlyToDisabledLibraries_ShouldDeny()
        {
            var person = CreatePerson("1001");
            var disabledMovie = CreateMovie(
                "B Disabled Movies",
                "/library/disabled-movies/movie-a.mkv",
                CreateCurrentPerson("1001", nameof(PersonKind.Actor), "角色A", "Actor A"));
            var disabledSeries = CreateSeries(
                "C Disabled Series",
                "/library/disabled-series/series-a",
                CreateCurrentPerson("1001", nameof(PersonKind.Actor), "角色A", "Actor A"));
            var resolver = CreateResolver(
                queryItems: new BaseItem[] { disabledMovie, disabledSeries },
                libraryOptionsByItem: new Dictionary<BaseItem, LibraryOptions?>
                {
                    [disabledMovie] = CreateLibraryOptions(nameof(Movie), metadataAllowed: false),
                    [disabledSeries] = CreateLibraryOptions(nameof(Series), metadataAllowed: false),
                });

            var decision = resolver.Resolve(person, MetaSharkLibraryCapability.Metadata);

            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.CapabilityDisabledForResolvedLibrary, decision.Reason);
            Assert.AreEqual(2, decision.ResolvedLibraries.Count);
            Assert.AreEqual("B Disabled Movies", decision.ResolvedLibraries[0].LibraryName);
            Assert.AreEqual(nameof(Movie), decision.ResolvedLibraries[0].ItemType);
            Assert.IsFalse(decision.ResolvedLibraries[0].MetadataAllowed);
            Assert.AreEqual("C Disabled Series", decision.ResolvedLibraries[1].LibraryName);
            Assert.AreEqual(nameof(Series), decision.ResolvedLibraries[1].ItemType);
            Assert.IsFalse(decision.ResolvedLibraries[1].MetadataAllowed);
        }

        [TestMethod]
        public void Resolve_PersonLinkedToEnabledAndDisabledLibraries_ShouldAllowAndPreserveSortedEvidence()
        {
            var person = CreatePerson("1001");
            var disabledMovie = CreateMovie(
                "B Disabled Movies",
                "/library/disabled-movies/movie-a.mkv",
                CreateCurrentPerson("1001", nameof(PersonKind.Actor), "角色A", "Actor A"));
            var enabledSeries = CreateSeries(
                "A Enabled Series",
                "/library/enabled-series/series-a",
                CreateCurrentPerson("1001", nameof(PersonKind.Actor), "角色A", "Actor A"));
            var resolver = CreateResolver(
                queryItems: new BaseItem[] { disabledMovie, enabledSeries },
                libraryOptionsByItem: new Dictionary<BaseItem, LibraryOptions?>
                {
                    [disabledMovie] = CreateLibraryOptions(nameof(Movie), metadataAllowed: false),
                    [enabledSeries] = CreateLibraryOptions(nameof(Series), metadataAllowed: true),
                });

            var decision = resolver.Resolve(person, MetaSharkLibraryCapability.Metadata);

            Assert.IsTrue(decision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.Allowed, decision.Reason);
            Assert.AreEqual(2, decision.ResolvedLibraries.Count);
            Assert.AreEqual("A Enabled Series", decision.ResolvedLibraries[0].LibraryName);
            Assert.AreEqual(nameof(Series), decision.ResolvedLibraries[0].ItemType);
            Assert.IsTrue(decision.ResolvedLibraries[0].MetadataAllowed);
            Assert.AreEqual("B Disabled Movies", decision.ResolvedLibraries[1].LibraryName);
            Assert.AreEqual(nameof(Movie), decision.ResolvedLibraries[1].ItemType);
            Assert.IsFalse(decision.ResolvedLibraries[1].MetadataAllowed);
        }

        [TestMethod]
        public void Resolve_UnresolvedPerson_ShouldReturnExplicitUnresolvedDenyReason()
        {
            var person = CreatePerson("1001");
            var resolver = CreateResolver(Array.Empty<BaseItem>(), new Dictionary<BaseItem, LibraryOptions?>());

            var decision = resolver.Resolve(person, MetaSharkLibraryCapability.Metadata);

            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.SharedEntityLibraryUnresolved, decision.Reason);
            Assert.AreEqual(MetaSharkLibraryCapability.Metadata, decision.Capability);
            Assert.AreEqual(0, decision.ResolvedLibraries.Count);
        }

        [TestMethod]
        public void Resolve_BoxSetLinkedOnlyToDisabledMovieLibrary_ShouldDeny()
        {
            var disabledMovie = CreateMovie(
                "B Disabled Movies",
                "/library/disabled-movies/movie-a.mkv",
                CreateCurrentPerson("9999", nameof(PersonKind.Actor), "角色X", "Actor X"));
            var boxSet = CreateBoxSet(disabledMovie);
            var resolver = CreateResolver(
                queryItems: Array.Empty<BaseItem>(),
                libraryOptionsByItem: new Dictionary<BaseItem, LibraryOptions?>
                {
                    [disabledMovie] = CreateLibraryOptions(nameof(Movie), metadataAllowed: false),
                },
                itemsById: new Dictionary<Guid, BaseItem>
                {
                    [disabledMovie.Id] = disabledMovie,
                });

            var decision = resolver.Resolve(boxSet, MetaSharkLibraryCapability.Metadata);

            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.CapabilityDisabledForResolvedLibrary, decision.Reason);
            Assert.AreEqual(1, decision.ResolvedLibraries.Count);
            Assert.AreEqual("B Disabled Movies", decision.ResolvedLibraries[0].LibraryName);
            Assert.AreEqual(nameof(Movie), decision.ResolvedLibraries[0].ItemType);
            Assert.IsFalse(decision.ResolvedLibraries[0].MetadataAllowed);
        }

        [TestMethod]
        public void Resolve_BoxSetLinkedToAtLeastOneEnabledMovieLibrary_ShouldAllow()
        {
            var disabledMovie = CreateMovie(
                "B Disabled Movies",
                "/library/disabled-movies/movie-a.mkv",
                CreateCurrentPerson("9999", nameof(PersonKind.Actor), "角色X", "Actor X"));
            var enabledMovie = CreateMovie(
                "A Enabled Movies",
                "/library/enabled-movies/movie-b.mkv",
                CreateCurrentPerson("8888", nameof(PersonKind.Actor), "角色Y", "Actor Y"));
            var boxSet = CreateBoxSet(disabledMovie, enabledMovie);
            var resolver = CreateResolver(
                queryItems: Array.Empty<BaseItem>(),
                libraryOptionsByItem: new Dictionary<BaseItem, LibraryOptions?>
                {
                    [disabledMovie] = CreateLibraryOptions(nameof(Movie), metadataAllowed: false),
                    [enabledMovie] = CreateLibraryOptions(nameof(Movie), metadataAllowed: true),
                },
                itemsById: new Dictionary<Guid, BaseItem>
                {
                    [disabledMovie.Id] = disabledMovie,
                    [enabledMovie.Id] = enabledMovie,
                });

            var decision = resolver.Resolve(boxSet, MetaSharkLibraryCapability.Metadata);

            Assert.IsTrue(decision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.Allowed, decision.Reason);
            Assert.AreEqual(2, decision.ResolvedLibraries.Count);
            Assert.AreEqual("A Enabled Movies", decision.ResolvedLibraries[0].LibraryName);
            Assert.IsTrue(decision.ResolvedLibraries[0].MetadataAllowed);
            Assert.AreEqual("B Disabled Movies", decision.ResolvedLibraries[1].LibraryName);
            Assert.IsFalse(decision.ResolvedLibraries[1].MetadataAllowed);
        }

        private static BoxSet CreateBoxSet(params BaseItem[] linkedItems)
        {
            var boxSet = new BoxSet
            {
                Id = Guid.NewGuid(),
                Name = "Collection A",
                Path = "/collections/collection-a",
            };

            SetLinkedChildren(boxSet, linkedItems);
            return boxSet;
        }

        private static Person CreatePerson(string tmdbPersonId)
        {
            var person = new Person
            {
                Id = Guid.NewGuid(),
                Name = "Actor A",
                Path = "/config/metadata/People/A/Actor A",
            };

            person.SetProviderId(MetadataProvider.Tmdb, tmdbPersonId);
            return person;
        }

        private static RelatedMovie CreateMovie(string name, string path, params PersonInfo[] people)
        {
            var movie = new RelatedMovie
            {
                Id = Guid.NewGuid(),
                Name = name,
                Path = path,
            };

            movie.SetSimulatedPeople(people);
            return movie;
        }

        private static RelatedSeries CreateSeries(string name, string path, params PersonInfo[] people)
        {
            var series = new RelatedSeries
            {
                Id = Guid.NewGuid(),
                Name = name,
                Path = path,
            };

            series.SetSimulatedPeople(people);
            return series;
        }

        private static PersonInfo CreateCurrentPerson(string tmdbPersonId, string personTypeName, string role, string name)
        {
            var person = new PersonInfo
            {
                Name = name,
                Role = role,
            };

            var typeProperty = typeof(PersonInfo).GetProperty(nameof(PersonInfo.Type), BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(typeProperty);
            typeProperty!.SetValue(person, Enum.Parse(typeProperty.PropertyType, personTypeName, ignoreCase: false));
            person.SetProviderId(MetadataProvider.Tmdb, tmdbPersonId);
            return person;
        }

        private static MetaSharkSharedEntityLibraryCapabilityResolver CreateResolver(
            IEnumerable<BaseItem> queryItems,
            IDictionary<BaseItem, LibraryOptions?> libraryOptionsByItem,
            IDictionary<Guid, BaseItem>? itemsById = null)
        {
            var queryResults = queryItems.ToList();
            var itemsByIdMap = itemsById ?? queryResults.ToDictionary(item => item.Id, item => item);
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(queryResults);
            libraryManagerStub
                .Setup(x => x.GetItemById(It.IsAny<Guid>()))
                .Returns((Guid itemId) => itemsByIdMap.TryGetValue(itemId, out var item) ? item : null!);
            libraryManagerStub
                .Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>()))
                .Returns((BaseItem item) => libraryOptionsByItem.TryGetValue(item, out var libraryOptions) ? libraryOptions! : null!);

            return new MetaSharkSharedEntityLibraryCapabilityResolver(libraryManagerStub.Object);
        }

        private static LibraryOptions CreateLibraryOptions(string itemType, bool metadataAllowed, bool imageAllowed = false)
        {
            return new LibraryOptions
            {
                TypeOptions = new[]
                {
                    new TypeOptions
                    {
                        Type = itemType,
                        MetadataFetchers = metadataAllowed ? new[] { MetaSharkPlugin.PluginName } : Array.Empty<string>(),
                        ImageFetchers = imageAllowed ? new[] { MetaSharkPlugin.PluginName } : Array.Empty<string>(),
                    },
                },
            };
        }

        private static void SetLinkedChildren(BoxSet boxSet, IEnumerable<BaseItem> linkedItems)
        {
            var linkedChildrenProperty = typeof(BoxSet).GetProperty("LinkedChildren", InstanceMemberBindingFlags);
            Assert.IsNotNull(linkedChildrenProperty, "BoxSet.LinkedChildren 属性不存在。 ");

            var linkedChildType = ResolveLinkedChildType(linkedChildrenProperty!.PropertyType);
            Assert.IsNotNull(linkedChildType, "无法解析 BoxSet.LinkedChildren 元素类型。 ");

            var linkedChildren = linkedItems
                .Select(item => CreateLinkedChild(linkedChildType!, item.Id))
                .ToArray();
            var linkedChildrenValue = CreateLinkedChildrenValue(linkedChildrenProperty.PropertyType, linkedChildType!, linkedChildren);
            linkedChildrenProperty.SetValue(boxSet, linkedChildrenValue);
        }

        private static object CreateLinkedChildrenValue(Type propertyType, Type linkedChildType, object[] linkedChildren)
        {
            var arrayType = linkedChildType.MakeArrayType();
            if (propertyType.IsAssignableFrom(arrayType))
            {
                var array = Array.CreateInstance(linkedChildType, linkedChildren.Length);
                for (var i = 0; i < linkedChildren.Length; i++)
                {
                    array.SetValue(linkedChildren[i], i);
                }

                return array;
            }

            var listType = typeof(List<>).MakeGenericType(linkedChildType);
            if (propertyType.IsAssignableFrom(listType))
            {
                var list = (IList)Activator.CreateInstance(listType)!;
                foreach (var linkedChild in linkedChildren)
                {
                    list.Add(linkedChild);
                }

                return list;
            }

            var value = Activator.CreateInstance(propertyType);
            Assert.IsNotNull(value, "无法创建 BoxSet.LinkedChildren 容器实例。 ");

            if (value is IList nonGenericList)
            {
                foreach (var linkedChild in linkedChildren)
                {
                    nonGenericList.Add(linkedChild);
                }

                return value!;
            }

            var addMethod = propertyType.GetMethod("Add", new[] { linkedChildType });
            Assert.IsNotNull(addMethod, "BoxSet.LinkedChildren 容器不支持 Add。 ");
            foreach (var linkedChild in linkedChildren)
            {
                addMethod!.Invoke(value, new[] { linkedChild });
            }

            return value!;
        }

        private static object CreateLinkedChild(Type linkedChildType, Guid itemId)
        {
            var linkedChild = Activator.CreateInstance(linkedChildType);
            Assert.IsNotNull(linkedChild, "无法创建 LinkedChild 实例。 ");

            var itemIdProperty = linkedChildType.GetProperty(nameof(LinkedChild.ItemId), InstanceMemberBindingFlags);
            Assert.IsNotNull(itemIdProperty, "LinkedChild.ItemId 属性不存在。 ");
            itemIdProperty!.SetValue(linkedChild, itemId);
            return linkedChild!;
        }

        private static Type? ResolveLinkedChildType(Type propertyType)
        {
            if (propertyType.IsArray)
            {
                return propertyType.GetElementType();
            }

            if (propertyType.IsGenericType)
            {
                return propertyType.GetGenericArguments().FirstOrDefault();
            }

            return typeof(LinkedChild);
        }

        private sealed class RelatedMovie : Movie
        {
            private readonly List<object> simulatedPeople = new List<object>();

            public void SetSimulatedPeople(IEnumerable<PersonInfo> people)
            {
                this.simulatedPeople.Clear();
                foreach (var person in people)
                {
                    this.simulatedPeople.Add(person);
                }
            }

            private IEnumerable GetPeople()
            {
                return this.simulatedPeople;
            }
        }

        private sealed class RelatedSeries : Series
        {
            private readonly List<object> simulatedPeople = new List<object>();

            public void SetSimulatedPeople(IEnumerable<PersonInfo> people)
            {
                this.simulatedPeople.Clear();
                foreach (var person in people)
                {
                    this.simulatedPeople.Add(person);
                }
            }

            private IEnumerable GetPeople()
            {
                return this.simulatedPeople;
            }
        }
    }
}
